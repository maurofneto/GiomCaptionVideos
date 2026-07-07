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
/// </summary>
public class ChannelProcessor(
    IVideoSourceService videoSource,
    IProgressoRepository progressoRepository,
    IVideoHandler videoHandler,
    ITracingService tracingService,
    ISessionContext sessionContext,
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
        DefinirSessaoAtual(correlationId, horaExecucao, config);

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

        while (processedIds.Count < _options.MaxVideosPerRun && !ct.IsCancellationRequested)
        {
            logger.LogInformation($"Canal {config.Name} — processando página (já processados: {processedIds.Count})", config.Name);

            using var tracePagina = tracingService.IniciarTrace("pipeline.pagina", "video_pipeline");
            DefinirSessaoAtual(correlationId, horaExecucao, config, playlistId, pageToken ?? "primeira");

            var page = await videoSource.GetPlaylistPageAsync(playlistId, Math.Min(50, _options.MaxVideosPerRun), pageToken, ct);
            if (page.Videos.Count == 0) break;

            foreach (var video in page.Videos)
            {
                if (processedIds.Count >= _options.MaxVideosPerRun || ct.IsCancellationRequested) break;

                if (applyDateFilter && video.UploadedAt <= (filtroDataBase ?? DateTime.MinValue)) continue;

                if (!videoHandler.IsRelevant(video)) continue;

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

                await Task.Delay(_options.DelayBetweenVideosMs, ct);
            }

            pageToken = page.NextPageToken;
            await progressoRepository.SaveProgressoAsync(channelKey, playlistId, pageToken, lastProcessedDate, ct);

            logger.LogInformation("Canal {Name} — página processada (próxima página: {NextPageToken})", config.Name, pageToken ?? "fim");

            if (string.IsNullOrEmpty(pageToken))
            {
                // Fim da playlist — sem isso, o while continuava rodando (processedIds.Count
                // ainda < MaxVideosPerRun) e reiniciava a paginação do zero pra sempre, num
                // loop infinito que nunca deixava o processo terminar.
                logger.LogInformation("Canal {Name} — playlist percorrida até o fim ({Count} processados)", config.Name, processedIds.Count);
                break;
            }

            await Task.Delay(_options.DelayBetweenPagesMs, ct);
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
