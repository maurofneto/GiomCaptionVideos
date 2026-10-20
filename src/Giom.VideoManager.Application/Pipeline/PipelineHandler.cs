using System.Collections.Concurrent;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Orquestrador central do pipeline (Feature #104) — único, não mais duplicado por canal. Escuta
/// eventos de "etapa concluída" e decide o que fazer a seguir a partir da topologia por canal,
/// lida do banco (<see cref="ITopologiaRepository"/>, tabela etapa_topologia — Config First, com
/// cache curto): publica na(s) próxima(s) fila(s) de etapa (fan-out, se a
/// lista tiver mais de 1 item) ou marca ProcessedAt se for terminal (lista vazia). Nenhum worker
/// de etapa decide isso sozinho — "quem manda" é só aqui, um lugar só.
/// </summary>
public class PipelineHandler(
    IVideoRepository videoRepository,
    IVideoEtapaTerminalRepository etapaTerminalRepository,
    ITopologiaRepository topologiaRepository,
    IServiceProvider serviceProvider,
    ILogger<PipelineHandler> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>Quanto tempo a topologia de um canal fica em memória antes de reler do banco — mudar etapa_topologia pega em ~1 min, sem deploy.</summary>
    public static readonly TimeSpan TtlTopologia = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _tempo = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (TopologiaCanal Topologia, DateTimeOffset Expira)> _cache = new();

    private async Task<TopologiaCanal?> ObterTopologiaAsync(string channelId, CancellationToken ct)
    {
        var agora = _tempo.GetUtcNow();
        if (_cache.TryGetValue(channelId, out var cacheado) && cacheado.Expira > agora)
            return cacheado.Topologia;

        // Falha de leitura propaga (a mensagem volta pra fila e é reentregue); canal sem linhas
        // devolve null e NÃO é cacheado, pra uma linha recém-inserida valer na próxima mensagem.
        var topologia = await topologiaRepository.ObterAsync(channelId, ct);
        if (topologia is not null)
            _cache[channelId] = (topologia, agora + TtlTopologia);
        return topologia;
    }

    public async Task ProcessarAsync(EtapaConcluidaMensagem mensagem, CancellationToken ct)
    {
        var video = mensagem.Video;

        var topologia = await ObterTopologiaAsync(video.ChannelId, ct)
            ?? throw new InvalidOperationException($"Canal sem topologia no orquestrador: ChannelId={video.ChannelId} ({video.ChannelTitle}) — falta em etapa_topologia");

        var canal = video.ChannelTitle;

        // Etapa fora da topologia do canal e etapa com lista vazia são tratadas igual: terminal.
        var proximasEtapas = topologia.ObterProximas(mensagem.Etapa);

        if (proximasEtapas.Length == 0)
        {
            // Bug #111: uma topologia com fan-out real (ex: Instituto: transcricao -> [resumo,
            // embedding], onde embedding e resumo->categorizacao são DUAS folhas terminais
            // separadas) só pode marcar ProcessedAt quando TODOS os ramos terminais concluírem —
            // não só o primeiro a chegar aqui. Só entra nesse rastreamento se mensagem.Etapa for
            // uma etapa terminal DE VERDADE (parte do universo conhecido da topologia do canal);
            // uma etapa fora desse universo (ver EtapaSemEntradaNaTopologia_TrataComoTerminal) não
            // tem como ser "esperada" em nenhum conjunto, então continua marcando na hora, como
            // sempre foi.
            var etapasTerminais = topologia.ObterEtapasTerminais();
            if (etapasTerminais.Contains(mensagem.Etapa))
            {
                var concluidas = await etapaTerminalRepository.RegistrarConclusaoAsync(video.Id, mensagem.Etapa, ct);
                if (!etapasTerminais.IsSubsetOf(concluidas))
                {
                    logger.LogInformation("{Canal} — ramo terminal {Etapa} concluído, aguardando outros ramos ({Concluidas}/{Total}): {Title}",
                        canal, mensagem.Etapa, concluidas.Count, etapasTerminais.Count, video.Title);
                    return;
                }
            }

            video.ProcessedAt = DateTime.UtcNow;
            await videoRepository.UpsertAsync(video, ct);
            logger.LogInformation("{Canal} — pipeline concluído (terminal em {Etapa}): {Title}", canal, mensagem.Etapa, video.Title);
            return;
        }

        foreach (var proximaEtapa in proximasEtapas)
        {
            var publisher = serviceProvider.GetRequiredKeyedService<IFilaPublisher<VideoEtapaMensagem>>(proximaEtapa);
            await publisher.PublicarAsync(new VideoEtapaMensagem(video, mensagem.Sessao, mensagem.TraceParent), ct);
            logger.LogInformation("{Canal} — {EtapaAnterior} concluída, publicando {ProximaEtapa}: {Title}", canal, mensagem.Etapa, proximaEtapa, video.Title);
        }
    }
}
