using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Ministerio.Worker;

/// <summary>
/// Dono da descoberta do canal Ministério (Feature #33 → #104) — desde a #104, só cuida de
/// ingestão: filtra relevância e publica na fila única de Transcrição (compartilhada com o
/// Instituto — Video.ChannelId já identifica de quem é o vídeo). O processamento das etapas
/// saiu daqui — vive nos workers de etapa, orquestrado pelo PipelineHandler central.
/// </summary>
public class MinisterioPipelineHandler(
    [FromKeyedServices("transcricao")] IFilaPublisher<VideoEtapaMensagem> transcricaoPublisher,
    ISessionContext sessionContext,
    ITracingService tracingService)
    : IVideoHandler
{
    /// <summary>Mesmo critério de sempre — título indica culto/ministério.</summary>
    public bool IsRelevant(Video video)
    {
        var title = video.Title.ToUpperInvariant();
        return title.Contains("CULTO") || title.Contains("MINISTÉRIO") || title.StartsWith("GIOM");
    }

    public Task HandleAsync(Video video, CancellationToken ct = default) =>
        transcricaoPublisher.PublicarAsync(new VideoEtapaMensagem(video, sessionContext.Atual, tracingService.CapturarContextoAtual()), ct);
}
