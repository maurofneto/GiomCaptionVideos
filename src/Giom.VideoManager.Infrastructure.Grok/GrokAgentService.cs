using System.Net.Http.Headers;
using System.Text;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Grok;

public class GrokAgentOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "grok-3-mini";
    public int MaxTokens { get; init; } = 4096;
}

/// <summary>
/// Agent de perguntas e respostas via xAI Grok — elo de fallback (Feature #101) atrás de
/// Claude. Mesmo prompt (agent.md) e mesmo formato de histórico que ClaudeAgentService.
/// </summary>
public class GrokAgentService : IAgentService
{
    private readonly HttpClient _http;
    private readonly GrokAgentOptions _options;
    private readonly ILogger<GrokAgentService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly IIncertezaDetectorService _incertezaDetector;
    private readonly ISessionContext _sessionContext;
    private readonly string _systemPrompt;
    private readonly IPricingService _pricingService;

    public GrokAgentService(IOptions<GrokAgentOptions> options, PromptLoader prompts, IHttpClientFactory httpClientFactory, ILogger<GrokAgentService> logger, IContextoTelemetria contextoTelemetria, IIncertezaDetectorService incertezaDetector, ISessionContext sessionContext, IPricingService pricingService)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _incertezaDetector = incertezaDetector;
        _sessionContext = sessionContext;
        _pricingService = pricingService;
        _http = httpClientFactory.CreateClient("grok");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        _systemPrompt = prompts.Load("agent.md");
    }

    public async Task<RespostaAgente> ResponderAsync(string chatId, TurnoConversa turnoAtual, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString();

        _sessionContext.Definir(new SessaoObservabilidade(
            SessionId: chatId,
            SessionName: "Bot Instituto Giom",
            SessionPath: "/bot/pergunta",
            UserId: chatId,
            Propriedades: new Dictionary<string, string> { ["Tipo"] = "bot_resposta" },
            RequestId: requestId));

        var mensagens = new List<object> { new { role = "system", content = _systemPrompt } };

        foreach (var turno in historico)
        {
            mensagens.Add(new { role = "user", content = turno.Pergunta });
            mensagens.Add(new { role = "assistant", content = turno.Resposta ?? string.Empty });
        }

        var userMessage = $"""
            Contexto das aulas:
            {contexto}

            Pergunta do aluno:
            {turnoAtual.Pergunta}
            """;
        mensagens.Add(new { role = "user", content = userMessage });

        var payload = new { model = _options.Model, max_tokens = _options.MaxTokens, messages = mensagens };
        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("v1/chat/completions", content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new RateLimitExcedidoException("Grok.Agent");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Grok agent falhou: {response.StatusCode} — {json}", inner: null, statusCode: response.StatusCode);

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
            custoEstimadoUsd: await _pricingService.CalcularAsync("Grok", _options.Model, result?.usage?.prompt_tokens, result?.usage?.completion_tokens, ct),
            detalhes: new { finishReason = result?.choices?.FirstOrDefault()?.finish_reason, incerteza = texto != null && _incertezaDetector.ContemIncerteza(texto) },
            requestIdExterno: requestId);

        if (string.IsNullOrWhiteSpace(texto))
        {
            // Lança em vez de devolver uma resposta genérica — dentro da cadeia de fallback
            // (Feature #101), "sem texto" precisa contar como falha pra passar pro próximo
            // provider, não virar a resposta final silenciosamente.
            _logger.LogWarning("Grok não retornou texto para {ChatId} — pergunta: {Pergunta}", chatId, turnoAtual.Pergunta);
            throw new InvalidOperationException("Grok não retornou texto");
        }

        return new RespostaAgente(texto);
    }
}
