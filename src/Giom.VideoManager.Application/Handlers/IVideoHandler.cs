using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Application.Handlers;

/// <summary>
/// Define o que fazer com cada vídeo após ser coletado pelo ChannelProcessor.
/// MinisterioVideoHandler: legenda + resumo do culto.
/// InstitutoVideoHandler: transcrição + resumo + categoria + embedding.
/// </summary>
public interface IVideoHandler
{
    /// <summary>Decide se o vídeo deve ser processado (filtro de relevância).</summary>
    bool IsRelevant(Video video);

    Task HandleAsync(Video video, CancellationToken ct = default);
}
