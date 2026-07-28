using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Web;

namespace Giom.VideoManager.Infrastructure.YouTube;

public class YouTubeTranscriptOptions
{
    /// <summary>Idiomas preferidos em ordem de prioridade</summary>
    public string[] Languages { get; init; } = ["pt", "pt-BR", "en"];
}

/// <summary>
/// Obtém transcrições diretamente do endpoint interno do YouTube via HttpClient.
/// Não usa a YouTube Data API nem ferramentas externas — zero quota, resposta em milissegundos.
///
/// Fluxo:
///   1. Faz GET na página do vídeo e extrai ytInitialPlayerResponse
///   2. Localiza a URL da faixa de legenda preferida (manual > automática)
///   3. Baixa o VTT e converte para texto limpo
/// </summary>
public class YouTubeTranscriptService : ICaptionService
{
    private readonly YouTubeTranscriptOptions _options;
    private readonly ILogger<YouTubeTranscriptService> _logger;
    private readonly HttpClient _http;

    // ytInitialPlayerResponse é um objeto JS inline na página do vídeo
    private static readonly Regex PlayerResponseRegex = new(
        @"ytInitialPlayerResponse\s*=\s*(\{.+?\});\s*(?:var\s+(?:meta|head)|</script>)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public YouTubeTranscriptService(IOptions<YouTubeTranscriptOptions> options, ILogger<YouTubeTranscriptService> logger)
    {
        _options = options.Value;
        _logger = logger;

        _http = new HttpClient();
        // User-Agent necessário — YouTube bloqueia requests sem ele
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "pt-BR,pt;q=0.9,en;q=0.8");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        // 1. Carrega a página do vídeo
        var html = await FetchPageAsync(video.Id, ct);
        if (html == null) throw new NenhumResultadoEncontradoException("YouTubeTranscript", $"falha ao carregar a página do vídeo {video.Id}");

        // 2. Extrai ytInitialPlayerResponse
        var captionTracks = ExtractCaptionTracks(html);
        if (captionTracks == null || !captionTracks.Any())
        {
            _logger.LogDebug("Nenhuma faixa de legenda encontrada para {VideoId}", video.Id);
            throw new NenhumResultadoEncontradoException("YouTubeTranscript", $"nenhuma faixa de legenda encontrada pro vídeo {video.Id}");
        }

        // 3. Escolhe a melhor faixa pelo idioma preferido (manual > automática)
        var track = SelectBestTrack(captionTracks);
        if (track == null) throw new NenhumResultadoEncontradoException("YouTubeTranscript", $"nenhuma faixa no idioma preferido pro vídeo {video.Id}");

        // 4. Baixa o VTT
        var vtt = await DownloadVttAsync(track.Url, ct);
        if (vtt == null) throw new NenhumResultadoEncontradoException("YouTubeTranscript", $"falha ao baixar o VTT do vídeo {video.Id}");

        // 5. Converte VTT → texto limpo
        var texto = ParseVtt(vtt);
        if (string.IsNullOrWhiteSpace(texto)) throw new NenhumResultadoEncontradoException("YouTubeTranscript", $"VTT do vídeo {video.Id} veio vazio após parse");

        _logger.LogInformation("Transcrição obtida para {VideoId} — idioma: {Lang}, automática: {Auto}, {Chars} chars",
            video.Id, track.LanguageCode, track.IsAuto, texto.Length);

        // ParseVtt já entrega texto limpo — ConteudoLimpo == Conteudo para este serviço
        return new Transcricao
        {
            VideoId       = video.Id,
            Conteudo      = texto,
            ConteudoLimpo = texto,
            Formato       = "vtt",
            Origem        = "YouTubeTranscript",
            GeradoEm      = DateTime.UtcNow
        };
    }

    // =========================================================
    // Passo 1: página do vídeo
    // =========================================================

    private async Task<string?> FetchPageAsync(string videoId, CancellationToken ct)
    {
        var url = $"https://www.youtube.com/watch?v={videoId}";
        try
        {
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Falha ao carregar página do vídeo {VideoId}: {Status}", videoId, response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao carregar página do vídeo {VideoId}", videoId);
            return null;
        }
    }

    // =========================================================
    // Passo 2: extrai faixas de legenda do ytInitialPlayerResponse
    // =========================================================

    private List<CaptionTrack>? ExtractCaptionTracks(string html)
    {
        var match = PlayerResponseRegex.Match(html);
        if (!match.Success)
        {
            _logger.LogDebug("ytInitialPlayerResponse não encontrado na página");
            return null;
        }

        try
        {
            var json = JObject.Parse(match.Groups[1].Value);
            var tracks = json
                .SelectTokens("captions.playerCaptionsTracklistRenderer.captionTracks[*]")
                .ToList();

            return tracks.Select(t => new CaptionTrack(
                Url:          t["baseUrl"]?.ToString() ?? "",
                LanguageCode: t["languageCode"]?.ToString() ?? "",
                IsAuto:       t["kind"]?.ToString() == "asr"
            )).Where(t => !string.IsNullOrEmpty(t.Url)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao parsear ytInitialPlayerResponse");
            return null;
        }
    }

    // =========================================================
    // Passo 3: escolhe a melhor faixa
    // Prioridade: idioma preferido manual > idioma preferido automático
    // =========================================================

    private CaptionTrack? SelectBestTrack(List<CaptionTrack> tracks)
    {
        foreach (var lang in _options.Languages)
        {
            // Prefere legenda manual (não ASR) no idioma
            var manual = tracks.FirstOrDefault(t =>
                t.LanguageCode.Equals(lang, StringComparison.OrdinalIgnoreCase) && !t.IsAuto);
            if (manual != null) return manual;
        }

        foreach (var lang in _options.Languages)
        {
            // Aceita automática no idioma
            var auto = tracks.FirstOrDefault(t =>
                t.LanguageCode.Equals(lang, StringComparison.OrdinalIgnoreCase) && t.IsAuto);
            if (auto != null) return auto;
        }

        // Qualquer faixa disponível
        return tracks.FirstOrDefault();
    }

    // =========================================================
    // Passo 4: baixa o VTT
    // =========================================================

    private async Task<string?> DownloadVttAsync(string baseUrl, CancellationToken ct)
    {
        // Adiciona fmt=vtt à URL da faixa
        var builder = new UriBuilder(baseUrl);
        var query = HttpUtility.ParseQueryString(builder.Query);
        query["fmt"] = "vtt";
        builder.Query = query.ToString();

        try
        {
            return await _http.GetStringAsync(builder.Uri, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao baixar VTT");
            return null;
        }
    }

    // =========================================================
    // Passo 5: VTT → texto limpo
    // Remove timestamps, tags HTML e duplicações por janela deslizante
    // =========================================================

    private static string ParseVtt(string vtt)
    {
        var lines = vtt.Split('\n', StringSplitOptions.TrimEntries);
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("WEBVTT") ||
                line.StartsWith("NOTE") ||
                line.Contains("-->") ||
                string.IsNullOrWhiteSpace(line))
                continue;

            // Remove tags HTML e timestamps inline: <00:01:23.456><c>texto</c>
            var clean = Regex.Replace(line, @"<[^>]+>", "").Trim();
            if (string.IsNullOrWhiteSpace(clean)) continue;

            if (seen.Add(clean))
                parts.Add(clean);
        }

        return string.Join(" ", parts);
    }

    private record CaptionTrack(string Url, string LanguageCode, bool IsAuto);
}
