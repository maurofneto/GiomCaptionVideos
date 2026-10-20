using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.YtDlp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Audio;

public class YtDlpAudioOptions
{
    /// <summary>Caminho para o executável yt-dlp.</summary>
    public string YtDlpPath { get; init; } = "yt-dlp";

    /// <summary>Fallback legado (US #144) — um único arquivo de cookies compartilhado. Preferir
    /// <see cref="IChannelYtDlpCookies"/> por ChannelId (US #146). Vazio = sem --cookies.</summary>
    public string CookiesFilePath { get; set; } = string.Empty;
}

/// <summary>
/// Baixa o áudio com yt-dlp (<c>-x --audio-format mp3</c>, exige ffmpeg na imagem). Lógica movida
/// sem mudança de comportamento de WhisperTranscricaoService.DownloadAudioAsync (US #148) — agora
/// compartilhada por Whisper e GrokVoice. Diferença: captura o stderr do yt-dlp pra registrar o
/// motivo da falha (ex.: bloqueio anti-bot, Bug #144) em vez de só o exit code. Roda pelo
/// <see cref="YtDlpProcesso"/>, então proxy/PO token (<see cref="YtDlpOptions"/>) valem aqui também,
/// e bloqueio anti-bot vira <see cref="FalhaNaoTransitoriaException"/> (a fila manda direto pra DLQ).
/// </summary>
public class YtDlpAudioDownloader(
    IOptions<YtDlpAudioOptions> options,
    IOptions<YtDlpOptions> ytDlpOptions,
    IChannelYtDlpCookies channelCookies,
    ILogger<YtDlpAudioDownloader> logger) : IAudioDownloader
{
    private readonly YtDlpAudioOptions _options = options.Value;
    private readonly YtDlpOptions _ytDlpOptions = ytDlpOptions.Value;

    public async Task<string> BaixarAudioAsync(string videoId, string? channelId, string workDir, CancellationToken ct = default)
    {
        var outputTemplate = Path.Combine(workDir, "%(id)s.%(ext)s");
        var args = MontarArgumentos(videoId, outputTemplate, ResolverCookiesPath(channelId));

        logger.LogInformation("Baixando áudio do vídeo {VideoId} com yt-dlp", videoId);
        var (exitCode, stderr) = await YtDlpProcesso.ExecutarAsync(_options.YtDlpPath, args, _ytDlpOptions, ct);

        // Bloqueio anti-bot não melhora repetindo: sem isso virava NenhumResultadoEncontrado genérico
        // e a fila reprocessava a cadeia inteira até o MaxReceiveCount, sempre com o mesmo resultado.
        if (exitCode != 0 && YtDlpBloqueioBot.Detectar(stderr) is { } reason)
        {
            logger.LogError("yt-dlp bloqueado pelo YouTube (BloqueioBot, {Reason}) ao baixar áudio do vídeo {VideoId} ChannelId={ChannelId} — falha não transitória, sem retry",
                reason, videoId, channelId);
            throw new FalhaNaoTransitoriaException("YtDlpAudio",
                $"yt-dlp bloqueado pelo YouTube (código {exitCode}) ao baixar áudio do vídeo {videoId}", reason);
        }

        if (exitCode != 0)
        {
            logger.LogWarning("yt-dlp falhou (código {ExitCode}) ao baixar áudio de {VideoId}: {Motivo}",
                exitCode, videoId, ResumirStderr(stderr));
            throw new NenhumResultadoEncontradoException("YtDlpAudio",
                $"yt-dlp falhou (código {exitCode}) ao baixar áudio do vídeo {videoId}");
        }

        var audioPath = Directory.GetFiles(workDir, "*.mp3").FirstOrDefault();
        return audioPath
            ?? throw new NenhumResultadoEncontradoException("YtDlpAudio", $"yt-dlp não gerou arquivo de áudio pro vídeo {videoId}");
    }

    /// <summary>Argumentos específicos do download de áudio — proxy/PO token entram em <see cref="YtDlpProcesso"/>.</summary>
    internal static List<string> MontarArgumentos(string videoId, string outputTemplate, string? cookiesPath)
    {
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(cookiesPath))
            args.AddRange(["--cookies", cookiesPath]);

        args.AddRange([
            "-x", "--audio-format", "mp3",
            "-o", outputTemplate,
            $"https://www.youtube.com/watch?v={videoId}"
        ]);

        return args;
    }

    private static string ResumirStderr(string stderr)
    {
        var linhas = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            .Take(2);
        var resumo = string.Join(" | ", linhas);
        return resumo.Length > 400 ? resumo[..400] : resumo;
    }

    private string? ResolverCookiesPath(string? channelId)
    {
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            var porCanal = channelCookies.GetCookiesFilePath(channelId);
            if (!string.IsNullOrWhiteSpace(porCanal)) return porCanal;
        }

        return string.IsNullOrWhiteSpace(_options.CookiesFilePath) ? null : _options.CookiesFilePath;
    }
}
