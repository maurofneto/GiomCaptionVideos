using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Obtém a legenda/caption de um vídeo diretamente da plataforma (sem download de áudio).
/// Implementações: YouTubeCaptionService
/// </summary>
public interface ICaptionService
{
    Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default);
}
