using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Hierarquia de Spans (Sentry Performance): run (Transaction) -> canal -> playlist -> página ->
/// vídeo -> etapas (transcrição/resumo/categorização/embedding, abertas de dentro de cada
/// serviço via ITracingService.IniciarEtapaAtual). Cada nível é aninhado dentro do anterior
/// (Activity.Current ambient) — a duração de "canal" no Sentry já é a soma de tudo que
/// aconteceu dentro dele, sem precisar agregar nada manualmente.
/// Não tagueia mais Sentry/log manualmente nível por nível — SentryTracingService tagueia todo
/// Span automaticamente a partir do que estiver em ISessionContext.Atual (ver DefinirSessaoAtual),
/// e o SessionContextEnricher faz o mesmo pros logs. Um lugar só define os atributos; os três
/// destinos (Sentry, Helicone, logs) leem dali.
/// IVideoHandler aqui não é necessariamente quem processa de verdade (Instituto/Ministerio) —
/// pode ser FilaVideoHandler (US #39/#40), que enfileira em vez de processar, delegando o
/// processamento real pro EtapaQueueConsumer da etapa Transcrição (Feature #33). ChannelProcessor
/// não sabe (nem precisa saber) qual dos dois está por trás — é só mais um IVideoHandler, igual
/// sempre foi.
/// Desde a US #105, o handler é resolvido por canal (resolverHandler) em vez de injetado fixo —
/// uma run pode processar N canais, cada um com seu próprio IVideoHandler (regra de relevância
/// diferente). ActiveChannelContext (AsyncLocal) também é definido aqui por canal, pra
/// YouTubeVideoSourceService saber qual credencial Google keyed usar.
/// </summary>
public class ChannelProcessor(
    IVideoSourceService videoSource,
    IProgressoRepository progressoRepository,
    Func<string, IVideoHandler> resolverHandler,
    ITracingService tracingService,
    ISessionContext sessionContext,
    IActiveChannelContext activeChannelContext,
    IHostEnvironment hostEnvironment,
    ILogger<ChannelProcessor> logger,
    IOptions<ProcessingOptions> options)
{
    private readonly ProcessingOptions _options = options.Value;

    public async Task RunAsync(IEnumerable<ChannelConfig> channels, CancellationToken ct = default)
    {
        // Um único CorrelationId pra execução inteira (todos os canais processados nesta run) —
        // é o mesmo Guid usado como Session-Id no Helicone e como tag correlation.id em todo Span
        // do Sentry, permitindo achar a mesma execução nas duas plataformas (e nos logs).
        var correlationId = Guid.NewGuid().ToString();
        var horaExecucao = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

        using var traceRun = tracingService.IniciarTrace("pipeline.run", "video_pipeline");

        foreach (var config in channels)
        {
            logger.LogInformation("Iniciando canal: {Name}", config.Name);
            try
            {
                await ProcessChannelAsync(config, correlationId, horaExecucao, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao processar canal {Name}", config.Name);
            }
        }

        logger.LogInformation("Processamento concluído.");
    }

    private async Task ProcessChannelAsync(ChannelConfig config, string correlationId, string horaExecucao, CancellationToken ct)
    {
        using var traceCanal = tracingService.IniciarTrace("pipeline.canal", "video_pipeline");
        activeChannelContext.Definir(config.ChannelId);
        DefinirSessaoAtual(correlationId, horaExecucao, config);

        var videoHandler = resolverHandler(config.ChannelId);

        // Overrides por canal (US #105, ChannelConfig.MaxVideosPerRun/DelayBetween*) — quando
        // null (ChannelConfig hardcoded, sem vir de canal_ingestao), cai no valor global de
        // ProcessingOptions, igual sempre foi.
        var maxVideosPerRun = config.MaxVideosPerRun ?? _options.MaxVideosPerRun;
        var delayEntreVideosMs = config.DelayBetweenVideosMs ?? _options.DelayBetweenVideosMs;
        var delayEntrePaginasMs = config.DelayBetweenPagesMs ?? _options.DelayBetweenPagesMs;

        var playlistId = await videoSource.GetUploadPlaylistIdAsync(config, ct);
        if (string.IsNullOrEmpty(playlistId))
        {
            logger.LogWarning("Playlist não encontrada para canal {Name}", config.Name);
            return;
        }

        using var tracePlaylist = tracingService.IniciarTrace("pipeline.playlist", "video_pipeline");
        DefinirSessaoAtual(correlationId, horaExecucao, config, playlistId);

        var channelKey       = config.ChannelId ?? "mine";
        var pageToken        = await progressoRepository.GetLastPageTokenAsync(channelKey, playlistId, ct);
        var lastProcessedDate = await progressoRepository.GetLastProcessedDateAsync(channelKey, playlistId, ct);
        var paginaOffset     = await progressoRepository.GetPaginaOffsetAsync(channelKey, playlistId, ct);

        bool applyDateFilter = pageToken == null;

        // Congela o corte de data usado só pra decidir o que pular — a playlist do YouTube
        // vem do mais novo pro mais antigo, então assim que o primeiro vídeo (o mais novo)
        // era processado, lastProcessedDate avançava pra aquela data e passava a filtrar
        // TODO o resto da mesma execução como "antigo demais", limitando cada run a 1 vídeo
        // processado, mesmo com MaxVideosPerRun bem maior e o progresso zerado.
        var filtroDataBase = lastProcessedDate;

        if (pageToken != null)
            logger.LogInformation("Canal {Name} — retomando da página salva (filtro de data desativado)", config.Name);
        else if (filtroDataBase.HasValue)
            logger.LogInformation("Canal {Name} — iniciando do topo, ignorando vídeos antes de {Date:d}", config.Name, filtroDataBase);

        var processedIds = new HashSet<string>();

        while (processedIds.Count < maxVideosPerRun && !ct.IsCancellationRequested)
        {
            logger.LogInformation($"Canal {config.Name} — processando página (já processados: {processedIds.Count})", config.Name);

            using var tracePagina = tracingService.IniciarTrace("pipeline.pagina", "video_pipeline");
            DefinirSessaoAtual(correlationId, horaExecucao, config, playlistId, pageToken ?? "primeira");

            var page = await videoSource.GetPlaylistPageAsync(playlistId, Math.Min(50, maxVideosPerRun), pageToken, ct);
            if (page.Videos.Count == 0) break;

            // Bug real (achado ao vivo, 2026-07-18/19): MaxVideosPerRun é um teto DURO — a
            // primeira versão deste fix (Bug #125) deixava a página terminar inteira mesmo
            // depois de bater o teto, o que estourava o limite (ex.: teto=2 processando 3) e
            // isso não é aceitável com rate limit real do YouTube/downstream. Mas simplesmente
            // cortar no meio (o bug ORIGINAL) e avançar pra próxima página também não serve —
            // os vídeos que sobrariam ficariam pra trás pra sempre. A correção certa: quando o
            // teto é batido no meio da página, NÃO avança pageToken (fica na mesma página) e
            // guarda paginaOffset = índice do primeiro vídeo ainda não avaliado — a próxima
            // execução refaz a MESMA página, pula direto pro índice salvo (sem re-gastar cota
            // reavaliando o que já foi visto) e só avança pra próxima página quando o laço
            // percorrer o restante dela inteiro sem bater no teto de novo.
            var paginaEsgotada = true;

            for (var indice = paginaOffset; indice < page.Videos.Count; indice++)
            {
                if (ct.IsCancellationRequested) { paginaEsgotada = false; paginaOffset = indice; break; }

                if (processedIds.Count >= maxVideosPerRun) { paginaEsgotada = false; paginaOffset = indice; break; }

                var video = page.Videos[indice];

                if (applyDateFilter && video.UploadedAt <= (filtroDataBase ?? DateTime.MinValue)) continue;

                if (!videoHandler.IsRelevant(video))
                {
                    logger.LogDebug("Canal {Name} — vídeo irrelevante, ignorando: {Id} — {Title}", config.Name, video.Id, video.Title);
                    continue;
                }

                if (!processedIds.Add(video.Id)) continue;

                logger.LogInformation("Canal {Name} — processando vídeo {Id} — {Title}", config.Name, video.Id, video.Title);

                using var traceVideo = tracingService.IniciarTrace("pipeline.video", "video_pipeline");
                DefinirSessaoAtual(correlationId, horaExecucao, config, playlistId, pageToken ?? "primeira", video);

                try
                {
                    await videoHandler.HandleAsync(video, ct);

                    if ((video.UploadedAt ?? DateTime.MinValue) > (lastProcessedDate ?? DateTime.MinValue))
                        lastProcessedDate = video.UploadedAt;
                }
                catch (Exception ex)
                {
                    traceVideo.MarcarErro(ex);
                    logger.LogError(ex, "Erro ao processar vídeo {Id} — {Title}", video.Id, video.Title);
                }

                await Task.Delay(delayEntreVideosMs, ct);
            }

            if (paginaEsgotada)
            {
                pageToken = page.NextPageToken;
                paginaOffset = 0;
            }
            else
            {
                logger.LogInformation("Canal {Name} — teto de {Max} atingido no meio da página (retomando no índice {Indice} na próxima execução)",
                    config.Name, maxVideosPerRun, paginaOffset);
            }

            await progressoRepository.SaveProgressoAsync(channelKey, playlistId, pageToken, lastProcessedDate, paginaOffset, ct);

            logger.LogInformation("Canal {Name} — página processada (próxima página: {NextPageToken})",
                config.Name, paginaEsgotada ? (pageToken ?? "fim") : "mesma página (retomando)");

            if (paginaEsgotada && string.IsNullOrEmpty(pageToken))
            {
                // Fim da playlist — sem isso, o while continuava rodando (processedIds.Count
                // ainda < MaxVideosPerRun) e reiniciava a paginação do zero pra sempre, num
                // loop infinito que nunca deixava o processo terminar.
                logger.LogInformation("Canal {Name} — playlist percorrida até o fim ({Count} processados)", config.Name, processedIds.Count);
                break;
            }

            await Task.Delay(delayEntrePaginasMs, ct);
        }
    }

    /// <summary>
    /// Único ponto que define o contexto de sessão/observabilidade — User/Session-Name/Path do
    /// Helicone, tags automáticas de todo Span do Sentry (via SentryTracingService) e propriedades
    /// de log (via SessionContextEnricher) vêm todos daqui. Cada nível da hierarquia (canal,
    /// playlist, página, vídeo) chama de novo, só acrescentando o que sabe de novo — nunca perde o
    /// que os níveis acima já definiram.
    /// </summary>
    private void DefinirSessaoAtual(string correlationId, string horaExecucao, ChannelConfig config,
        string? playlistId = null, string? pagina = null, Video? video = null)
    {
        var path = $"/pipeline-{hostEnvironment.EnvironmentName}/{config.Name}";
        var propriedades = new Dictionary<string, string>
        {
            ["Environment"] = hostEnvironment.EnvironmentName,
            ["Canal"] = config.Name
        };

        if (playlistId is not null)
        {
            path += $"/playlist-{playlistId}";
            propriedades["Playlist"] = playlistId;
        }

        if (pagina is not null)
        {
            path += $"/pagina-{pagina}";
            propriedades["Pagina"] = pagina;
        }

        if (video is not null)
        {
            path += $"/{video.Id}";
            propriedades["VideoId"] = video.Id;
            propriedades["VideoTitle"] = video.Title;
        }

        sessionContext.Definir(new SessaoObservabilidade(
            SessionId: correlationId,
            SessionName: horaExecucao,
            SessionPath: path,
            UserId: config.Name,
            Propriedades: propriedades));
    }
}
