using System.Diagnostics;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
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
/// motivo da falha (ex.: bloqueio anti-bot, Bug #144) em vez de só o exit code.
/// </summary>
public class YtDlpAudioDownloader(
    IOptions<YtDlpAudioOptions> options,
    IChannelYtDlpCookies channelCookies,
    ILogger<YtDlpAudioDownloader> logger) : IAudioDownloader
{
    private readonly YtDlpAudioOptions _options = options.Value;

    public async Task<string> BaixarAudioAsync(string videoId, string? channelId, string workDir, CancellationToken ct = default)
    {
        var outputTemplate = Path.Combine(workDir, "%(id)s.%(ext)s");
        var args = MontarArgumentos(videoId, outputTemplate, ResolverCookiesPath(channelId));

        logger.LogInformation("Baixando áudio do vídeo {VideoId} com yt-dlp", videoId);
        var (exitCode, stderr) = await RunProcessAsync(_options.YtDlpPath, args, ct);

        if (exitCode != 0)
        {
            logger.LogWarning("yt-dlp falhou (código {ExitCode}) ao baixar áudio de {VideoId}: {Motivo}",
                exitCode, videoId, ResumirStderr(stderr));
            throw new NenhumResultadoEncontradoException("YtDlpAudio",
                $"yt-dlp falhou (código {exitCode}) ao baixar áudio do vídeo {videoId}" +
                (IndicaBloqueioAntiBot(stderr) ? " — bloqueio anti-bot do YouTube (cookies ausentes ou inválidos)" : ""));
        }

        var audioPath = Directory.GetFiles(workDir, "*.mp3").FirstOrDefault();
        return audioPath
            ?? throw new NenhumResultadoEncontradoException("YtDlpAudio", $"yt-dlp não gerou arquivo de áudio pro vídeo {videoId}");
    }

    internal static string MontarArgumentos(string videoId, string outputTemplate, string? cookiesPath)
    {
        var cookiesArg = string.IsNullOrWhiteSpace(cookiesPath) ? "" : $"--cookies \"{cookiesPath}\" ";
        return $"{cookiesArg}-x --audio-format mp3 -o \"{outputTemplate}\" https://www.youtube.com/watch?v={videoId}";
    }

    internal static bool IndicaBloqueioAntiBot(string stderr) =>
        stderr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase);

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

    private static async Task<(int ExitCode, string Stderr)> RunProcessAsync(string executable, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stderrTask);
    }
}
