using System.Net.Http.Headers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Whisper;

public class WhisperOptions
{
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Modelo de transcrição da OpenAI: whisper-1, gpt-4o-transcribe, gpt-4o-mini-transcribe.</summary>
    public string Model { get; init; } = "whisper-1";

    /// <summary>Diretório temporário para download do áudio.</summary>
    public string TempDirectory { get; init; } = Path.GetTempPath();
}

/// <summary>
/// Baixa o áudio com yt-dlp e transcreve via API de áudio da OpenAI (não mais Whisper local via
/// CLI Python) — último recurso da cadeia de Transcrição (Feature #101), só acionado quando
/// nenhuma legenda existe em lugar nenhum.
///
/// Trocado de CLI local (yt-dlp + whisper + PyTorch, processos externos via
/// System.Diagnostics.Process) pra API: a imagem Docker do Transcricao.Worker
/// (mcr.microsoft.com/dotnet/aspnet:9.0) nunca teve Python/PyTorch/ffmpeg instalados — o Whisper
/// local NUNCA funcionou de verdade em produção (só não tinha sido percebido porque o Bug #134
/// escondia isso: a cadeia nunca chegava até aqui). US$0,006/minuto de áudio — trivial pra um
/// elo de último recurso, e elimina de vez a dependência pesada da imagem.
/// </summary>
public class WhisperTranscricaoService : ITranscricaoService
{
    // US$0,006/minuto pro whisper-1 e pro gpt-4o-transcribe (mesma tabela) — conferir
    // platform.openai.com/docs/pricing antes de confiar cegamente, tende a mudar.
    private const decimal PrecoUsdPorMinuto = 0.006m;

    private readonly WhisperOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<WhisperTranscricaoService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly IAudioDownloader _audioDownloader;

    public WhisperTranscricaoService(
        IOptions<WhisperOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<WhisperTranscricaoService> logger,
        IContextoTelemetria contextoTelemetria,
        IAudioDownloader audioDownloader)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _audioDownloader = audioDownloader;
        _http = httpClientFactory.CreateClient("whisper");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<Transcricao?> TranscreverAsync(string videoId, string? channelId = null, CancellationToken ct = default)
    {
        var workDir = Path.Combine(_options.TempDirectory, "giom_whisper", videoId);
        Directory.CreateDirectory(workDir);

        try
        {
            var audioPath = await _audioDownloader.BaixarAudioAsync(videoId, channelId, workDir, ct);
            var texto = await TranscreverViaApiAsync(audioPath, videoId, ct);

            return new Transcricao
            {
                VideoId       = videoId,
                Conteudo      = texto,
                ConteudoLimpo = texto, // saída da API já é texto limpo, sem timestamps/tags
                Formato       = "txt",
                Origem        = "Whisper",
                GeradoEm      = DateTime.UtcNow
            };
        }
        finally
        {
            TryCleanup(workDir);
        }
    }

    private async Task<string> TranscreverViaApiAsync(string audioPath, string videoId, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(audioPath, ct);
        var streamContent = new ByteArrayContent(bytes);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(streamContent, "file", Path.GetFileName(audioPath));
        form.Add(new StringContent(_options.Model), "model");
        form.Add(new StringContent("pt"), "language");
        form.Add(new StringContent("verbose_json"), "response_format"); // verbose_json traz "duration" — precisamos pra estimar custo

        _logger.LogInformation("Transcrevendo via API da OpenAI ({Model}): {VideoId}", _options.Model, videoId);
        var response = await _http.PostAsync("v1/audio/transcriptions", form, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new RateLimitExcedidoException("Whisper");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Whisper (API) falhou: {response.StatusCode} — {json}", inner: null, statusCode: response.StatusCode);

        var result = JsonConvert.DeserializeAnonymousType(json, new { text = "", duration = 0.0 });
        var texto = result?.text;
        var custoEstimadoUsd = result?.duration is { } segundos ? (decimal)(segundos / 60.0) * PrecoUsdPorMinuto : (decimal?)null;

        _contextoTelemetria.Registrar(
            provedor: "OpenAI",
            modelo: _options.Model,
            custoEstimadoUsd: custoEstimadoUsd,
            detalhes: new { origem = "Whisper", duracaoSegundos = result?.duration });

        if (string.IsNullOrWhiteSpace(texto))
            throw new NenhumResultadoEncontradoException("Whisper", $"API devolveu transcrição vazia pro vídeo {videoId}");

        return texto;
    }

    private void TryCleanup(string workDir)
    {
        try { Directory.Delete(workDir, recursive: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Falha ao limpar diretório temporário {Dir}", workDir); }
    }
}
