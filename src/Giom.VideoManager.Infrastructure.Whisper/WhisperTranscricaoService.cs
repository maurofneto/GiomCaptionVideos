using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Whisper;

public class WhisperOptions
{
    /// <summary>Caminho para o executável yt-dlp</summary>
    public string YtDlpPath { get; init; } = "yt-dlp";

    /// <summary>Caminho para o executável whisper</summary>
    public string WhisperPath { get; init; } = "whisper";

    /// <summary>Modelo Whisper: tiny, base, small, medium, large</summary>
    public string Model { get; init; } = "medium";

    /// <summary>Diretório temporário para download e transcrição</summary>
    public string TempDirectory { get; init; } = Path.GetTempPath();
}

/// <summary>
/// Baixa o áudio com yt-dlp e transcreve com Whisper (OpenAI).
/// </summary>
public class WhisperTranscricaoService : ITranscricaoService
{
    private readonly WhisperOptions _options;
    private readonly ILogger<WhisperTranscricaoService> _logger;

    public WhisperTranscricaoService(IOptions<WhisperOptions> options, ILogger<WhisperTranscricaoService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Transcricao?> TranscreverAsync(string videoId, CancellationToken ct = default)
    {
        var workDir = Path.Combine(_options.TempDirectory, "giom_whisper", videoId);
        Directory.CreateDirectory(workDir);

        try
        {
            var audioPath = await DownloadAudioAsync(videoId, workDir, ct);
            if (audioPath == null) return null;

            var texto = await RunWhisperAsync(audioPath, workDir, ct);
            if (texto == null) return null;

            return new Transcricao
            {
                VideoId = videoId,
                Conteudo = texto,
                Formato = "txt",
                Origem = "Whisper",
                GeradoEm = DateTime.UtcNow
            };
        }
        finally
        {
            TryCleanup(workDir);
        }
    }

    private async Task<string?> DownloadAudioAsync(string videoId, string workDir, CancellationToken ct)
    {
        var outputTemplate = Path.Combine(workDir, "%(id)s.%(ext)s");
        var args = $"-x --audio-format mp3 -o \"{outputTemplate}\" https://www.youtube.com/watch?v={videoId}";

        _logger.LogInformation("Baixando áudio do vídeo {VideoId} com yt-dlp", videoId);
        var exitCode = await RunProcessAsync(_options.YtDlpPath, args, ct);

        if (exitCode != 0)
        {
            _logger.LogError("yt-dlp falhou com código {Code} para vídeo {VideoId}", exitCode, videoId);
            return null;
        }

        return Directory.GetFiles(workDir, "*.mp3").FirstOrDefault();
    }

    private async Task<string?> RunWhisperAsync(string audioPath, string workDir, CancellationToken ct)
    {
        var args = $"\"{audioPath}\" --model {_options.Model} --output_dir \"{workDir}\" --output_format txt --language Portuguese";

        _logger.LogInformation("Transcrevendo com Whisper: {AudioPath}", audioPath);
        var exitCode = await RunProcessAsync(_options.WhisperPath, args, ct);

        if (exitCode != 0)
        {
            _logger.LogError("Whisper falhou com código {Code}", exitCode);
            return null;
        }

        var txtFile = Directory.GetFiles(workDir, "*.txt").FirstOrDefault();
        return txtFile != null ? await File.ReadAllTextAsync(txtFile, ct) : null;
    }

    private static async Task<int> RunProcessAsync(string executable, string arguments, CancellationToken ct)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false
            }
        };

        process.Start();
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    private void TryCleanup(string workDir)
    {
        try { Directory.Delete(workDir, recursive: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Falha ao limpar diretório temporário {Dir}", workDir); }
    }
}
