using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Google.Apis.YouTube.v3;
using Giom.VideoManager.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.YouTube;

public class YouTubeCaptionOptions
{
    /// <summary>Formato da legenda baixada. Valores aceitos: vtt, sbv, srt. Padrão: vtt.</summary>
    public string Formato { get; init; } = "vtt";
}

/// <summary>
/// Baixa a legenda do vídeo diretamente da YouTube Data API (sem download de áudio).
/// </summary>
public class YouTubeCaptionService : ICaptionService
{
    private readonly ResilientYouTubeApiService _api;
    private readonly YouTubeService _youtube;
    private readonly YouTubeCaptionOptions _options;
    private readonly ILogger<YouTubeCaptionService> _logger;

    public YouTubeCaptionService(
        YouTubeService youtube,
        IOptions<YouTubeCaptionOptions> options,
        ILogger<YouTubeCaptionService> logger)
    {
        _youtube = youtube;
        _options = options.Value;
        _logger  = logger;
        _api     = new ResilientYouTubeApiService(youtube, Microsoft.Extensions.Logging.Abstractions.NullLogger<ResilientYouTubeApiService>.Instance);
    }

    public async Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        var listRequest  = _youtube.Captions.List("snippet", video.Id);
        var listResponse = await _api.ListCaptionsAsync(listRequest);

        var caption = listResponse.Items?.FirstOrDefault(c => c.Snippet?.Status != "failed");
        if (caption == null)
        {
            // Era LogDebug — invisível em prod (MinimumLevel=Information), o que escondia por
            // completo a causa de vídeos que ficam incompletos pra sempre (Bug #100). O handler
            // que chama isso já loga um Warning com o título quando Transcricao continua null —
            // aqui fica em Information pra não duplicar o nível de alerta, só dar o motivo exato.
            _logger.LogInformation("Nenhuma legenda disponível para vídeo {VideoId}", video.Id);
            return null;
        }

        var downloadRequest  = _youtube.Captions.Download(caption.Id);
        downloadRequest.Tfmt = _options.Formato;

        using var memory = new MemoryStream();
        await _api.DownloadCaptionAsync(downloadRequest, memory);

        var conteudo = System.Text.Encoding.UTF8.GetString(memory.ToArray());
        if (string.IsNullOrWhiteSpace(conteudo))
        {
            _logger.LogWarning("Legenda vazia para {VideoId}", video.Id);
            return null;
        }

        _logger.LogInformation("Legenda obtida via YouTubeCaption: {VideoId} ({Formato}, {Chars} chars)",
            video.Id, _options.Formato, conteudo.Length);

        return new Transcricao
        {
            VideoId       = video.Id,
            Conteudo      = conteudo,
            ConteudoLimpo = TranscricaoHelper.StripTimestamps(conteudo, _options.Formato),
            Formato       = _options.Formato,
            Origem        = "YouTubeCaption",
            GeradoEm      = DateTime.UtcNow
        };
    }
}
