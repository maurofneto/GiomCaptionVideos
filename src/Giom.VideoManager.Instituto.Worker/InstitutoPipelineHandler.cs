using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Instituto.Worker;

/// <summary>
/// Dono da descoberta do canal Instituto (Feature #33 → #104) — desde a #104, só cuida de
/// ingestão: filtra relevância e publica na fila única de Transcrição (compartilhada com o
/// Ministério — Video.ChannelId já identifica de quem é o vídeo). O processamento das etapas
/// (Transcrição/Resumo/Categorização/Embedding) saiu inteiro daqui — vive nos workers de etapa
/// (Transcricao.Worker, Resumo.Worker, etc.), orquestrado pelo PipelineHandler central. Esta
/// classe não implementa mais IEtapaXHandler nenhum.
/// </summary>
public class InstitutoPipelineHandler(
    [FromKeyedServices("transcricao")] IFilaPublisher<VideoEtapaMensagem> transcricaoPublisher,
    ISessionContext sessionContext,
    ITracingService tracingService)
    : IVideoHandler
{
    /// <summary>Mesmo critério de sempre — vídeos ao vivo não listados.</summary>
    public bool IsRelevant(Video video) => video.Type == VideoType.Live && video.PrivacyStatus == "unlisted";

    public Task HandleAsync(Video video, CancellationToken ct = default) =>
        transcricaoPublisher.PublicarAsync(new VideoEtapaMensagem(video, sessionContext.Atual, tracingService.CapturarContextoAtual()), ct);
}
