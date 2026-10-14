namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Baixa o áudio de um vídeo do YouTube para um arquivo local (mp3) — passo comum a todos os
/// provedores de transcrição por áudio (Whisper, GrokVoice). Extraído de WhisperTranscricaoService
/// (US #148) pra não duplicar a chamada ao yt-dlp nem criar referência direta entre projetos
/// Infrastructure.*. Implementação: YtDlpAudioDownloader (Infrastructure).
/// </summary>
public interface IAudioDownloader
{
    /// <param name="videoId">Id do vídeo no YouTube.</param>
    /// <param name="channelId">ChannelId do vídeo — escolhe os cookies yt-dlp do canal (US #146).</param>
    /// <param name="workDir">Diretório de trabalho (já criado); o chamador apaga ao terminar.</param>
    /// <returns>Caminho do arquivo mp3 baixado.</returns>
    /// <exception cref="Exceptions.NenhumResultadoEncontradoException">yt-dlp falhou ou não gerou arquivo.</exception>
    Task<string> BaixarAudioAsync(string videoId, string? channelId, string workDir, CancellationToken ct = default);
}
