using System.Net.Http.Headers;
using System.Text;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Giom.VideoManager.Infrastructure.Precos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.OpenAI;

public class OpenAiCategorizacaoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-4o-mini";
    public int MaxTokens { get; init; } = 512;
}

/// <summary>
/// Classifica via OpenAI chat completions — último elo da cadeia de fallback (Feature #101),
/// atrás de Claude e Grok. Prompt e Model/MaxTokens vêm de EtapaProvedorConfig, resolvida por
/// canal (US #106).
/// </summary>
public class OpenAiCategorizacaoService : ICategorizacaoProvedorService
{
    private readonly HttpClient _http;
    private readonly OpenAiCategorizacaoOptions _options;
    private readonly ILogger<OpenAiCategorizacaoService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly ISessionContext _sessionContext;
    private readonly ITracingService _tracingService;

    public OpenAiCategorizacaoService(IOptions<OpenAiCategorizacaoOptions> options, IHttpClientFactory httpClientFactory, ILogger<OpenAiCategorizacaoService> logger, IContextoTelemetria contextoTelemetria, ISessionContext sessionContext, ITracingService tracingService)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _sessionContext = sessionContext;
        _tracingService = tracingService;
        _http = httpClientFactory.CreateClient("openai");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, EtapaProvedorConfig config, CancellationToken ct = default)
    {
        var resumo = video.Resumo?.Descricao ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resumo)) return [];

        _sessionContext.Definir(_sessionContext.Atual.ParaEtapa("categorizacao", video));
        using var span = _tracingService.IniciarEtapaAtual("categorizacao");

        var modelo = config.Model ?? _options.Model;
        var maxTokens = config.MaxTokens ?? _options.MaxTokens;

        var payload = new
        {
            model = modelo,
            max_tokens = maxTokens,
            messages = new object[]
            {
                new { role = "system", content = config.Prompt },
                new { role = "user", content = resumo }
            }
        };

        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("v1/chat/completions", content, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new RateLimitExcedidoException("OpenAI.Categorizacao");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI categorização falhou: {response.StatusCode} — {responseJson}", inner: null, statusCode: response.StatusCode);

        var result = JsonConvert.DeserializeAnonymousType(responseJson, new
        {
            choices = new[] { new { message = new { content = "" }, finish_reason = "" } },
            usage = new { prompt_tokens = 0, completion_tokens = 0 }
        });

        var json = result?.choices?.FirstOrDefault()?.message.content;

        _contextoTelemetria.Registrar(
            provedor: "OpenAI",
            modelo: modelo,
            tokensEntrada: result?.usage?.prompt_tokens,
            tokensSaida: result?.usage?.completion_tokens,
            custoEstimadoUsd: PricingCalculator.Calcular("OpenAI", modelo, result?.usage?.prompt_tokens, result?.usage?.completion_tokens),
            detalhes: new { finishReason = result?.choices?.FirstOrDefault()?.finish_reason });

        if (string.IsNullOrWhiteSpace(json)) throw new NenhumResultadoEncontradoException("OpenAI.Categorizacao", $"resposta veio vazia pro vídeo {video.Id}");

        _logger.LogInformation("Categorização gerada via OpenAI para {VideoId}", video.Id);

        json = json.Replace("\n", "").Replace("\r", "").Replace("```json", "").Replace("```", "").Trim();

        var items = JsonConvert.DeserializeAnonymousType(json,
            new[] { new { area = "", subcategoria = "", peso = 0.0 } });

        if (items == null || items.Length == 0) throw new NenhumResultadoEncontradoException("OpenAI.Categorizacao", $"JSON sem itens pro vídeo {video.Id}: {json}");

        var categorias = new List<CategoriaTeologica>();
        foreach (var item in items)
        {
            if (!Enum.TryParse<AreaTeologica>(item.area, ignoreCase: true, out var area))
            {
                _logger.LogWarning("Área teológica desconhecida (OpenAI): {Area}", item.area);
                continue;
            }

            categorias.Add(new CategoriaTeologica
            {
                VideoId        = video.Id,
                Area           = area,
                Subcategoria   = item.subcategoria ?? string.Empty,
                Peso           = (decimal)Math.Clamp(item.peso, 0.01, 1.0),
                ClassificadoEm = DateTime.UtcNow
            });
        }

        if (categorias.Count == 0) throw new NenhumResultadoEncontradoException("OpenAI.Categorizacao", $"nenhuma área teológica reconhecida pro vídeo {video.Id}: {json}");

        return categorias.OrderByDescending(c => c.Peso).ToList();
    }
}
