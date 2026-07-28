using System.Net.Http.Headers;
using System.Text;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Giom.VideoManager.Infrastructure.Precos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Kimi;

public class KimiResumoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "kimi-k3";
    public int MaxTokens { get; init; } = 4096;
}

/// <summary>
/// Gera resumo via Kimi K3 (Moonshot) — elo de fallback (US #124) atrás de Claude/Grok/OpenAI.
/// API compatível com o formato de chat completions da OpenAI (sem SDK .NET oficial da
/// Moonshot — HTTP cru, mesmo estilo de GrokResumoService). Prompt e Model/MaxTokens vêm de
/// EtapaProvedorConfig, resolvida por canal (US #106).
///
/// Particularidades do K3 (platform.kimi.ai/docs/guide/kimi-k3-quickstart): "thinking" sempre
/// ligado — a resposta separa `reasoning_content` de `content`; só `content` interessa aqui, o
/// raciocínio nunca vira parte do resumo. Sampling (temperature/top_p/penalties) é fixo do lado
/// do provedor, por isso não é enviado no payload — diferente de Claude/Grok/OpenAI, não dá pra
/// ajustar via EtapaProvedorConfig. O campo de limite de saída também tem nome próprio
/// (`max_completion_tokens`, não `max_tokens`).
/// </summary>
public class KimiResumoService : IResumoProvedorService
{
    private readonly HttpClient _http;
    private readonly KimiResumoOptions _options;
    private readonly ILogger<KimiResumoService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly ISessionContext _sessionContext;
    private readonly ITracingService _tracingService;

    public KimiResumoService(IOptions<KimiResumoOptions> options, IHttpClientFactory httpClientFactory, ILogger<KimiResumoService> logger, IContextoTelemetria contextoTelemetria, ISessionContext sessionContext, ITracingService tracingService)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _sessionContext = sessionContext;
        _tracingService = tracingService;
        _http = httpClientFactory.CreateClient("kimi");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<Resumo?> GerarResumoAsync(Video video, EtapaProvedorConfig config, CancellationToken ct = default)
    {
        var textoLimpo = video.Transcricao?.ConteudoLimpo;
        if (string.IsNullOrWhiteSpace(textoLimpo))
            return null;

        _sessionContext.Definir(_sessionContext.Atual.ParaEtapa("resumo", video));
        using var span = _tracingService.IniciarEtapaAtual("resumo");

        var modelo = config.Model ?? _options.Model;
        var maxTokens = config.MaxTokens ?? _options.MaxTokens;

        var payload = new
        {
            model = modelo,
            max_completion_tokens = maxTokens,
            messages = new object[]
            {
                new { role = "system", content = config.Prompt },
                new { role = "user", content = textoLimpo }
            }
        };

        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("v1/chat/completions", content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new RateLimitExcedidoException("Kimi.Resumo");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Kimi resumo falhou: {response.StatusCode} — {json}", inner: null, statusCode: response.StatusCode);

        var result = JsonConvert.DeserializeAnonymousType(json, new
        {
            choices = new[] { new { message = new { content = "" }, finish_reason = "" } },
            usage = new { prompt_tokens = 0, completion_tokens = 0 }
        });

        var texto = result?.choices?.FirstOrDefault()?.message.content;

        _contextoTelemetria.Registrar(
            provedor: "Kimi",
            modelo: modelo,
            tokensEntrada: result?.usage?.prompt_tokens,
            tokensSaida: result?.usage?.completion_tokens,
            custoEstimadoUsd: PricingCalculator.Calcular("Kimi", modelo, result?.usage?.prompt_tokens, result?.usage?.completion_tokens),
            detalhes: new { finishReason = result?.choices?.FirstOrDefault()?.finish_reason });

        if (string.IsNullOrWhiteSpace(texto)) throw new NenhumResultadoEncontradoException("Kimi.Resumo", $"resposta veio vazia pro vídeo {video.Id}");

        _logger.LogInformation("Resumo gerado via Kimi para {VideoId}", video.Id);

        return new Resumo
        {
            VideoId   = video.Id,
            Descricao = texto,
            Origem    = "Kimi",
            GeradoEm  = DateTime.UtcNow,
            Capitulos = []
        };
    }
}
