using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.YtDlp;
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

    /// <summary>Fallback legado (US #144) — um único arquivo compartilhado. Preferir
    /// <see cref="IChannelYtDlpCookies"/> por ChannelId (US #146). Vazio = sem --cookies.</summary>
    public string CookiesFilePath { get; set; } = string.Empty;
}

/// <summary>
/// Baixa legendas automáticas geradas pelo YouTube usando yt-dlp.
/// Não consome quota da YouTube Data API.
/// Funciona para qualquer vídeo que tenha auto-legendas ativas.
/// </summary>
public class YtDlpCaptionService : ICaptionService
{
    private readonly YtDlpCaptionOptions _options;
    private readonly YtDlpOptions _ytDlpOptions;
    private readonly ILogger<YtDlpCaptionService> _logger;
    private readonly ITracingService _tracingService;
    private readonly IChannelYtDlpCookies _channelCookies;

    public YtDlpCaptionService(
        IOptions<YtDlpCaptionOptions> options,
        IOptions<YtDlpOptions> ytDlpOptions,
        ILogger<YtDlpCaptionService> logger,
        ITracingService tracingService,
        IChannelYtDlpCookies channelCookies)
    {
        _options = options.Value;
        _ytDlpOptions = ytDlpOptions.Value;
        _logger = logger;
        _tracingService = tracingService;
        _channelCookies = channelCookies;
    }

    public async Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        var workDir = Path.Combine(_options.TempDirectory, "giom_captions", video.Id);
        Directory.CreateDirectory(workDir);

        // Diferente das chamadas HTTP (instrumentadas via OpenTelemetry.Instrumentation.Http),
        // invocar o yt-dlp é um Process externo — sem este Span, essa perna do fallback de
        // transcrição ficava totalmente invisível no Sentry (nem duração, nem sucesso/falha).
        using var span = _tracingService.IniciarEtapaAtual("transcricao_ytdlp");

        try
        {
            var args = MontarArgumentos(video, workDir);

            _logger.LogInformation("Baixando legendas via yt-dlp: {VideoId}", video.Id);
            var (exitCode, stderr) = await YtDlpProcesso.ExecutarAsync(_options.YtDlpPath, args, _ytDlpOptions, ct);
            Activity.Current?.SetTag("exit_code", exitCode);

            if (ClassificadorFalhaCaption.ClassificarSaidaYtDlp(exitCode, stderr) is { } bloqueio)
            {
                FalhaCaptionLog.Registrar(_logger, bloqueio, Provedor, video.Id, video.ChannelId);
                var motivo = $"yt-dlp bloqueado pelo YouTube (código {exitCode}) pro vídeo {video.Id}";
                var naoTransitoria = ClassificadorFalhaCaption.EhNaoTransitoria(bloqueio.Classe)
                    ? new FalhaNaoTransitoriaException(Provedor, motivo, bloqueio.Reason)
                    : null;
                throw new CaptionFalhaException(bloqueio.Classe, Provedor, motivo, reason: bloqueio.Reason, innerException: naoTransitoria);
            }

            if (exitCode == 0 && ClassificadorFalhaCaption.ClassificarStderrYtDlp(stderr) is not null)
                _logger.LogInformation("yt-dlp emitiu aviso de bloqueio mas terminou com sucesso (código 0) para vídeo {VideoId}", video.Id);

            if (exitCode != 0)
            {
                _logger.LogWarning("yt-dlp falhou (código {Code}) para vídeo {VideoId}", exitCode, video.Id);
                throw new NenhumResultadoEncontradoException("YtDlpCaption", $"yt-dlp falhou (código {exitCode}) pro vídeo {video.Id}");
            }

            var vttFile = FindVttFile(workDir);
            if (vttFile == null)
            {
                _logger.LogDebug("Nenhuma legenda encontrada para vídeo {VideoId}", video.Id);
                throw new NenhumResultadoEncontradoException("YtDlpCaption", $"nenhum arquivo .vtt gerado pro vídeo {video.Id}");
            }

            var rawVtt = await File.ReadAllTextAsync(vttFile, ct);
            var texto = ParseVtt(rawVtt);

            if (string.IsNullOrWhiteSpace(texto)) throw new NenhumResultadoEncontradoException("YtDlpCaption", $"VTT do vídeo {video.Id} veio vazio após parse");

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

    private const string Provedor = "YtDlpCaption";

    /// <summary>Argumentos específicos desta chamada — proxy/PO token entram em <see cref="YtDlpProcesso"/>.</summary>
    internal List<string> MontarArgumentos(Video video, string workDir)
    {
        var args = new List<string>();

        var cookiesPath = ResolverCookiesPath(video.ChannelId);
        if (!string.IsNullOrWhiteSpace(cookiesPath))
            args.AddRange(["--cookies", cookiesPath]);

        // Tenta legendas manuais primeiro, depois automáticas
        args.AddRange([
            "--write-subs", "--write-auto-subs",
            "--sub-lang", string.Join(",", _options.Languages),
            "--sub-format", "vtt", "--skip-download",
            "-o", Path.Combine(workDir, "%(id)s.%(ext)s"),
            $"https://www.youtube.com/watch?v={video.Id}"
        ]);

        return args;
    }

    private string? ResolverCookiesPath(string? channelId)
    {
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            var porCanal = _channelCookies.GetCookiesFilePath(channelId);
            if (!string.IsNullOrWhiteSpace(porCanal))
                return porCanal;
        }

        return string.IsNullOrWhiteSpace(_options.CookiesFilePath) ? null : _options.CookiesFilePath;
    }

    private void TryCleanup(string workDir)
    {
        try { Directory.Delete(workDir, recursive: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Falha ao limpar diretório temporário {Dir}", workDir); }
    }
}
