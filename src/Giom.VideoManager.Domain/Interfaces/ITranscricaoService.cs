using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Transcreve o áudio de um vídeo (download + speech-to-text).
/// Implementações: WhisperTranscricaoService
/// </summary>
public interface ITranscricaoService
{
    /// <param name="channelId">ChannelId do vídeo — usado pra cookies yt-dlp por canal (US #146).</param>
    Task<Transcricao?> TranscreverAsync(string videoId, string? channelId = null, CancellationToken ct = default);
}
