using System.Net;
using System.Net.Http.Headers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.GrokVoice;

public class GrokVoiceOptions
{
    /// <summary>Chave da API xAI. Valor real só no SSM (<c>/giom/{env}/pipeline/GrokVoice/ApiKey</c>).</summary>
    public string ApiKey { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = "https://api.x.ai/";

    /// <summary>Modelo de speech-to-text da xAI.</summary>
    public string Model { get; init; } = "grok-voice-transcribe-2.0";

    /// <summary>Idioma esperado (ISO 639-1). Fixo em pt evita detecção errada em trechos de canto/música.</summary>
    public string Language { get; init; } = "pt";

    /// <summary>Diretório temporário para o áudio baixado.</summary>
    public string TempDirectory { get; init; } = Path.GetTempPath();
}

/// <summary>
/// Transcreve o ÁUDIO do vídeo via Grok Voice (xAI, <c>POST /v1/stt</c>) — elo da cadeia de
/// Transcrição (US #148), antes do Whisper. Aceita o arquivo inteiro numa requisição (testado com
/// mp3 de 64,5 MB / 1h45), sem dividir em pedaços como a API da OpenAI (limite de 25 MB), e não
/// alucinou nos trechos sem fala em que o Whisper repetia "Se inscreva no canal...".
/// O download do áudio (yt-dlp, mesmo gargalo anti-bot do Bug #144) vem de <see cref="IAudioDownloader"/>.
/// </summary>
public class GrokVoiceCaptionService : ICaptionService
{
    private readonly GrokVoiceOptions _options;
    private readonly HttpClient _http;
    private readonly IAudioDownloader _audioDownloader;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly IPricingService _pricingService;
    private readonly ILogger<GrokVoiceCaptionService> _logger;

    public GrokVoiceCaptionService(
        IOptions<GrokVoiceOptions> options,
        IHttpClientFactory httpClientFactory,
        IAudioDownloader audioDownloader,
        IContextoTelemetria contextoTelemetria,
        IPricingService pricingService,
        ILogger<GrokVoiceCaptionService> logger)
    {
        _options = options.Value;
        _http = httpClientFactory.CreateClient("grokvoice");
        _audioDownloader = audioDownloader;
        _contextoTelemetria = contextoTelemetria;
        _pricingService = pricingService;
        _logger = logger;
    }

    public async Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("GrokVoice:ApiKey não configurada (SSM /giom/{env}/pipeline/GrokVoice/ApiKey).");

        var workDir = Path.Combine(_options.TempDirectory, "giom_grokvoice", video.Id);
        Directory.CreateDirectory(workDir);

        try
        {
            var audioPath = await _audioDownloader.BaixarAudioAsync(video.Id, video.ChannelId, workDir, ct);
            var resposta = await TranscreverAsync(audioPath, video.Id, ct);

            var texto = GrokVoiceTextoPosProcessador.Limpar(resposta.Text);
            if (string.IsNullOrWhiteSpace(texto))
                throw new NenhumResultadoEncontradoException("GrokVoice", $"API devolveu transcrição vazia (ou só música) pro vídeo {video.Id}");

            return new Transcricao
            {
                VideoId       = video.Id,
                Conteudo      = texto,
                ConteudoLimpo = texto,
                Formato       = "txt",
                Origem        = "GrokVoice",
                GeradoEm      = DateTime.UtcNow
            };
        }
        finally
        {
            TryCleanup(workDir);
        }
    }

    private async Task<GrokSttResposta> TranscreverAsync(string audioPath, string videoId, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var arquivo = new ByteArrayContent(await File.ReadAllBytesAsync(audioPath, ct));
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(new StringContent(_options.Model), "model");
        form.Add(new StringContent("true"), "format"); // pontuação e capitalização
        form.Add(new StringContent(_options.Language), "language");
        form.Add(arquivo, "file", Path.GetFileName(audioPath));

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/stt") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        _logger.LogInformation("Transcrevendo via Grok Voice ({Model}): {VideoId}", _options.Model, videoId);
        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitExcedidoException("GrokVoice");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GrokVoice (API) falhou: {(int)response.StatusCode} {response.StatusCode} — {Resumir(json)}",
                inner: null, statusCode: response.StatusCode);

        var resposta = JsonConvert.DeserializeObject<GrokSttResposta>(json)
            ?? throw new NenhumResultadoEncontradoException("GrokVoice", $"resposta inválida da API pro vídeo {videoId}");

        _contextoTelemetria.Registrar(
            provedor: "xAI",
            modelo: _options.Model,
            // Config First: preço por hora vem de provedor_preco.por_hora (xAI / modelo), não do código.
            custoEstimadoUsd: await _pricingService.CalcularPorHoraAsync("xAI", _options.Model, resposta.Duration, ct),
            detalhes: new { origem = "GrokVoice", duracaoSegundos = resposta.Duration, palavras = resposta.Words?.Count });

        return resposta;
    }

    private static string Resumir(string corpo) => corpo.Length > 300 ? corpo[..300] : corpo;

    private void TryCleanup(string workDir)
    {
        try { Directory.Delete(workDir, recursive: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Falha ao limpar diretório temporário {Dir}", workDir); }
    }

    private sealed class GrokSttResposta
    {
        [JsonProperty("text")] public string? Text { get; set; }
        [JsonProperty("language")] public string? Language { get; set; }
        [JsonProperty("duration")] public double? Duration { get; set; }
        [JsonProperty("words")] public List<GrokSttPalavra>? Words { get; set; }
    }

    private sealed class GrokSttPalavra
    {
        [JsonProperty("text")] public string? Text { get; set; }
        [JsonProperty("start")] public double Start { get; set; }
        [JsonProperty("end")] public double End { get; set; }
    }
}
