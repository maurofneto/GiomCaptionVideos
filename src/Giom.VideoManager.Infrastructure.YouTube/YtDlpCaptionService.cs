using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Giom.VideoManager.Infrastructure.YouTube;

public class YtDlpCaptionOptions
{
    /// <summary>Caminho para o executável yt-dlp</summary>
    public string YtDlpPath { get; init; } = "yt-dlp";

    /// <summary>Idiomas de legenda a tentar, em ordem de preferência</summary>
    public string[] Languages { get; init; } = ["pt", "pt-BR", "en"];

    /// <summary>Diretório temporário para os arquivos .vtt baixados</summary>
    public string TempDirectory { get; init; } = Path.GetTempPath();
}

/// <summary>
/// Baixa legendas automáticas geradas pelo YouTube usando yt-dlp.
/// Não consome quota da YouTube Data API.
/// Funciona para qualquer vídeo que tenha auto-legendas ativas.
/// </summary>
public class YtDlpCaptionService : ICaptionService
{
    private readonly YtDlpCaptionOptions _options;
    private readonly ILogger<YtDlpCaptionService> _logger;

    public YtDlpCaptionService(IOptions<YtDlpCaptionOptions> options, ILogger<YtDlpCaptionService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        var workDir = Path.Combine(_options.TempDirectory, "giom_captions", video.Id);
        Directory.CreateDirectory(workDir);

        try
        {
            var langs = string.Join(",", _options.Languages);
            var outputTemplate = Path.Combine(workDir, "%(id)s.%(ext)s");
            var url = $"https://www.youtube.com/watch?v={video.Id}";

            // Tenta legendas manuais primeiro, depois automáticas
            var args = $"--write-subs --write-auto-subs --sub-lang \"{langs}\" " +
                       $"--sub-format vtt --skip-download -o \"{outputTemplate}\" {url}";

            _logger.LogInformation("Baixando legendas via yt-dlp: {VideoId}", video.Id);
            var exitCode = await RunProcessAsync(_options.YtDlpPath, args, ct);

            if (exitCode != 0)
            {
                _logger.LogWarning("yt-dlp falhou (código {Code}) para vídeo {VideoId}", exitCode, video.Id);
                return null;
            }

            var vttFile = FindVttFile(workDir);
            if (vttFile == null)
            {
                _logger.LogDebug("Nenhuma legenda encontrada para vídeo {VideoId}", video.Id);
                return null;
            }

            var rawVtt = await File.ReadAllTextAsync(vttFile, ct);
            var texto = ParseVtt(rawVtt);

            if (string.IsNullOrWhiteSpace(texto)) return null;

            _logger.LogInformation("Legenda obtida via yt-dlp: {VideoId} ({Chars} chars)", video.Id, texto.Length);

            // ParseVtt já entrega texto limpo — ConteudoLimpo == Conteudo para este serviço
            return new Transcricao
            {
                VideoId       = video.Id,
                Conteudo      = texto,
                ConteudoLimpo = texto,
                Formato       = "vtt",
                Origem        = "YtDlpCaption",
                GeradoEm      = DateTime.UtcNow
            };
        }
        finally
        {
            TryCleanup(workDir);
        }
    }

    // =====================================================
    // Escolhe o arquivo .vtt preferindo o idioma declarado
    // =====================================================

    private string? FindVttFile(string workDir)
    {
        var files = Directory.GetFiles(workDir, "*.vtt");
        if (files.Length == 0) return null;

        // Prefere o primeiro idioma configurado
        foreach (var lang in _options.Languages)
        {
            var match = files.FirstOrDefault(f =>
                Path.GetFileName(f).Contains($".{lang}.", StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return files[0];
    }

    // =====================================================
    // Converte VTT → texto limpo (remove timestamps e tags)
    // O VTT do YouTube tem linhas duplicadas por janela
    // deslizante — deduplicamos para evitar repetição.
    // =====================================================

    private static string ParseVtt(string vtt)
    {
        var lines = vtt.Split('\n', StringSplitOptions.TrimEntries);
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();

        foreach (var line in lines)
        {
            // Pula cabeçalho, timestamps e linhas vazias
            if (line.StartsWith("WEBVTT") ||
                line.StartsWith("NOTE") ||
                line.Contains("-->") ||
                string.IsNullOrWhiteSpace(line))
                continue;

            // Remove tags HTML do YouTube: <00:01:23.456><c>texto</c>
            var clean = System.Text.RegularExpressions.Regex
                .Replace(line, @"<[^>]+>", "")
                .Trim();

            if (string.IsNullOrWhiteSpace(clean)) continue;

            // Deduplicação de linhas repetidas pela janela deslizante do VTT
            if (seen.Add(clean))
                parts.Add(clean);
        }

        return string.Join(" ", parts);
    }

    private static async Task<int> RunProcessAsync(string executable, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName  = executable,
                Arguments = arguments,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
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
