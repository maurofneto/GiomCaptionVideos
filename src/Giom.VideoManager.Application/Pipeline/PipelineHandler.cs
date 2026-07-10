using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Orquestrador central do pipeline (Feature #104) — único, não mais duplicado por canal. Escuta
/// eventos de "etapa concluída" e decide o que fazer a seguir a partir da topologia por canal
/// (<see cref="TopologiaOptions"/>): publica na(s) próxima(s) fila(s) de etapa (fan-out, se a
/// lista tiver mais de 1 item) ou marca ProcessedAt se for terminal (lista vazia). Nenhum worker
/// de etapa decide isso sozinho — "quem manda" é só aqui, um lugar só.
/// </summary>
public class PipelineHandler(
    IVideoRepository videoRepository,
    IOptions<TopologiaOptions> options,
    IServiceProvider serviceProvider,
    ILogger<PipelineHandler> logger)
{
    private readonly TopologiaOptions _options = options.Value;

    public async Task ProcessarAsync(EtapaConcluidaMensagem mensagem, CancellationToken ct)
    {
        var video = mensagem.Video;

        if (!_options.Canais.TryGetValue(video.ChannelId, out var canal))
            throw new InvalidOperationException($"Canal desconhecido pro orquestrador: ChannelId={video.ChannelId} ({video.ChannelTitle}) — falta em Topologia:Canais");

        if (!_options.Topologia.TryGetValue(canal, out var porEtapa) || !porEtapa.TryGetValue(mensagem.Etapa, out var proximasEtapas))
            throw new InvalidOperationException($"Topologia não configurada pro canal {canal}, etapa {mensagem.Etapa}");

        if (proximasEtapas.Length == 0)
        {
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
