using Domain.Entities;
using Domain.Interfaces;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Infrastructure.YouTube;

/// <summary>
/// Baixa a legenda/caption do vídeo diretamente da YouTube API (sem download de áudio).
/// </summary>
public class YouTubeCaptionService : ICaptionService
{
    private readonly ResilientYouTubeApiService _api;
    private readonly YouTubeService _youtube;
    private readonly ILogger<YouTubeCaptionService> _logger;

    // SBV timestamp: 0:01:06.155,0:01:08.175
    private static readonly Regex SbvTimestampRegex = new(
        @"^\d+:\d{2}:\d{2}\.\d{3},\d+:\d{2}:\d{2}\.\d{3}$",
        RegexOptions.Compiled);

    public YouTubeCaptionService(YouTubeService youtube, ILogger<YouTubeCaptionService> logger)
    {
        _youtube = youtube;
        _logger = logger;
        _api = new ResilientYouTubeApiService(youtube, Microsoft.Extensions.Logging.Abstractions.NullLogger<ResilientYouTubeApiService>.Instance);
    }

    public async Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        var listRequest = _youtube.Captions.List("snippet", video.Id);
        var listResponse = await _api.ListCaptionsAsync(listRequest);

        var caption = listResponse.Items?.FirstOrDefault(c => c.Snippet?.Status != "failed");
        if (caption == null)
        {
            _logger.LogDebug("Nenhuma legenda disponível para vídeo {VideoId}", video.Id);
            return null;
        }

        var downloadRequest = _youtube.Captions.Download(caption.Id);
        downloadRequest.Tfmt = "vtt";

        using var memory = new MemoryStream();
        await _api.DownloadCaptionAsync(downloadRequest, memory);

        var rawSbv = System.Text.Encoding.UTF8.GetString(memory.ToArray());
        var texto  = ParseSbv(rawSbv);

        if (string.IsNullOrWhiteSpace(texto))
        {
            _logger.LogWarning("Legenda SBV vazia após parsing para {VideoId}", video.Id);
            return null;
        }

        _logger.LogInformation("Legenda obtida via YouTubeCaption: {VideoId} ({Chars} chars)", video.Id, texto.Length);

        return new Transcricao
        {
            VideoId  = video.Id,
            Conteudo = texto,
            Formato  = "sbv",
            Origem   = "YouTubeCaption",
            GeradoEm = DateTime.UtcNow
        };
    }

    // SBV tem blocos:
    //   0:01:06.155,0:01:08.175   ← linha de timestamp (remover)
    //   texto da fala             ← manter
    //   (linha vazia)
    // O YouTube gera janela deslizante, então deduplica as linhas de texto.
    private static string ParseSbv(string sbv)
    {
        var lines = sbv.Split('\n', StringSplitOptions.TrimEntries);
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))         continue;
            if (SbvTimestampRegex.IsMatch(line))         continue;

            // Remove tags HTML inline que eventualmente aparecem no SBV
            var clean = Regex.Replace(line, @"<[^>]+>", "").Trim();
            if (string.IsNullOrWhiteSpace(clean))        continue;

            if (seen.Add(clean))
                parts.Add(clean);
        }

        return string.Join(" ", parts);
    }
}
