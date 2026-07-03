using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Transcreve o áudio de um vídeo (download + speech-to-text).
/// Implementações: WhisperTranscricaoService
/// </summary>
public interface ITranscricaoService
{
    Task<Transcricao?> TranscreverAsync(string videoId, CancellationToken ct = default);
}
