using System.Net.Http.Headers;
using System.Text;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.OpenAI;

public class OpenAiAgentOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-4o-mini";
    /// <summary>Teto de tokens da resposta (OpenAiAgent:MaxTokens). Precisa ser &gt; 0 — validado no startup.</summary>
    public int MaxTokens { get; init; } = 4096;

}

/// <summary>
/// Agent de perguntas e respostas via OpenAI chat completions — último elo da cadeia de
/// fallback (Feature #101), atrás de Claude e Grok. Mesmo prompt (agent.md) e mesmo formato de
/// histórico que ClaudeAgentService.
/// </summary>
public class OpenAiAgentService : IAgentService
{
    private readonly HttpClient _http;
    private readonly OpenAiAgentOptions _options;
    private readonly ILogger<OpenAiAgentService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly IIncertezaDetectorService _incertezaDetector;
    private readonly ISessionContext _sessionContext;
    private readonly string _promptBase;
    private readonly IAgenteConfigService _agenteConfig;

    public OpenAiAgentService(IOptions<OpenAiAgentOptions> options, PromptLoader prompts, IHttpClientFactory httpClientFactory, ILogger<OpenAiAgentService> logger, IContextoTelemetria contextoTelemetria, IIncertezaDetectorService incertezaDetector, ISessionContext sessionContext, IAgenteConfigService agenteConfig)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _incertezaDetector = incertezaDetector;
        _sessionContext = sessionContext;
        _http = httpClientFactory.CreateClient("openai");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        _promptBase = prompts.Load("agent.md");
        _agenteConfig = agenteConfig;
    }

    public async Task<RespostaAgente> ResponderAsync(string chatId, TurnoConversa turnoAtual, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString();

        // Config First: a instrução de concisão vem de agente_config (banco, cache de 1 min), não do appsettings.
        var instrucaoConcisao = await _agenteConfig.ObterInstrucaoConcisaoAsync("OpenAI", ct);
        var systemPrompt = PromptAgente.Montar(_promptBase, instrucaoConcisao);

        _sessionContext.Definir(new SessaoObservabilidade(
            SessionId: chatId,
            SessionName: "Bot Instituto Giom",
            SessionPath: "/bot/pergunta",
            UserId: chatId,
            Propriedades: new Dictionary<string, string> { ["Tipo"] = "bot_resposta" },
            RequestId: requestId));

        var mensagens = new List<object> { new { role = "system", content = systemPrompt } };

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
            throw new RateLimitExcedidoException("OpenAI.Agent");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI agent falhou: {response.StatusCode} — {json}", inner: null, statusCode: response.StatusCode);

        var result = JsonConvert.DeserializeAnonymousType(json, new
        {
            choices = new[] { new { message = new { content = "" }, finish_reason = "" } },
            usage = new { prompt_tokens = 0, completion_tokens = 0 }
        });

        var texto = result?.choices?.FirstOrDefault()?.message.content;

        _contextoTelemetria.Registrar(
            provedor: "OpenAI",
            modelo: _options.Model,
            tokensEntrada: result?.usage?.prompt_tokens,
            tokensSaida: result?.usage?.completion_tokens,
            detalhes: new
            {
                finishReason = result?.choices?.FirstOrDefault()?.finish_reason,
                incerteza = texto != null && _incertezaDetector.ContemIncerteza(texto),
                maxTokens = _options.MaxTokens,
                concisao = !string.IsNullOrWhiteSpace(instrucaoConcisao)
            },
            requestIdExterno: requestId);

        if (string.IsNullOrWhiteSpace(texto))
        {
            // Sem catch de "resposta vazia" aqui — este é o ÚLTIMO elo da cadeia (Feature #101),
            // então lançar propaga pro chamador de verdade (ChatBotProcessor já trata Exception
            // no nível mais alto e responde algo genérico ao usuário).
            _logger.LogWarning("OpenAI não retornou texto para {ChatId} — pergunta: {Pergunta}", chatId, turnoAtual.Pergunta);
            throw new InvalidOperationException("OpenAI não retornou texto");
        }

        return new RespostaAgente(texto);
    }
}
