using System.Net.Http.Headers;
using System.Text;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Grok;

public class GrokResumoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "grok-3-mini";
    public int MaxTokens { get; init; } = 4096;
}

/// <summary>
/// Gera resumo via xAI Grok — elo de fallback (Feature #101) atrás de Claude. API compatível com
/// o formato de chat completions da OpenAI (sem SDK .NET oficial da xAI — HTTP cru, mesmo estilo
/// de OpenAIEmbeddingService). Reaproveita o mesmo prompt de resumo.md via PromptLoader.
/// </summary>
public class GrokResumoService : IResumoService
{
    private readonly HttpClient _http;
    private readonly GrokResumoOptions _options;
    private readonly ILogger<GrokResumoService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly ISessionContext _sessionContext;
    private readonly ITracingService _tracingService;
    private readonly string _systemPrompt;

    public GrokResumoService(IOptions<GrokResumoOptions> options, PromptLoader prompts, IHttpClientFactory httpClientFactory, ILogger<GrokResumoService> logger, IContextoTelemetria contextoTelemetria, ISessionContext sessionContext, ITracingService tracingService)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _sessionContext = sessionContext;
        _tracingService = tracingService;
        _http = httpClientFactory.CreateClient("grok");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        _systemPrompt = prompts.Load("resumo.md");
    }

    public async Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
    {
        var textoLimpo = video.Transcricao?.ConteudoLimpo;
        if (string.IsNullOrWhiteSpace(textoLimpo))
            return null;

        _sessionContext.Definir(_sessionContext.Atual.ParaEtapa("resumo", video));
        using var span = _tracingService.IniciarEtapaAtual("resumo");

        var payload = new
        {
            model = _options.Model,
            max_tokens = _options.MaxTokens,
            messages = new object[]
            {
                new { role = "system", content = _systemPrompt },
                new { role = "user", content = textoLimpo }
            }
        };

        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("v1/chat/completions", content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new RateLimitExcedidoException("Grok.Resumo");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Grok resumo falhou: {response.StatusCode} — {json}", inner: null, statusCode: response.StatusCode);

        var result = JsonConvert.DeserializeAnonymousType(json, new
        {
            choices = new[] { new { message = new { content = "" }, finish_reason = "" } },
            usage = new { prompt_tokens = 0, completion_tokens = 0 }
        });

        var texto = result?.choices?.FirstOrDefault()?.message.content;

        _contextoTelemetria.Registrar(
            provedor: "Grok",
            modelo: _options.Model,
            tokensEntrada: result?.usage?.prompt_tokens,
            tokensSaida: result?.usage?.completion_tokens,
            detalhes: new { finishReason = result?.choices?.FirstOrDefault()?.finish_reason });

        if (string.IsNullOrWhiteSpace(texto)) return null;

        _logger.LogInformation("Resumo gerado via Grok para {VideoId}", video.Id);

        return new Resumo
        {
            VideoId   = video.Id,
            Descricao = texto,
            Origem    = "Grok",
            GeradoEm  = DateTime.UtcNow,
            Capitulos = []
        };
    }
}
