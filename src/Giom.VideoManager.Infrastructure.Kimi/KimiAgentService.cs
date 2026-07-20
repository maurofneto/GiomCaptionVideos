using System.Net.Http.Headers;
using System.Text;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Precos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Kimi;

public class KimiAgentOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "kimi-k3";
    public int MaxTokens { get; init; } = 4096;
}

/// <summary>
/// Agent de perguntas e respostas via Kimi K3 (Moonshot) — elo de fallback (US #126) atrás de
/// Claude/Grok/OpenAI. Mesmo prompt (agent.md) e mesmo formato de histórico dos outros agentes.
///
/// Particularidade real deste provedor: "thinking" sempre ligado — a resposta separa
/// `reasoning_content` de `content`, e a doc do Kimi exige preservar a mensagem completa do
/// assistente (incluindo o raciocínio) nas próximas chamadas da MESMA conversa, senão o modelo
/// perde coerência do que já "pensou". RespostaAgente.ReasoningContent (Domain) carrega esse
/// dado de volta pro chamador — ChatBotProcessor grava em TurnoConversa.ReasoningContent junto
/// com o texto, sem precisar de nenhum canal à parte.
/// </summary>
public class KimiAgentService : IAgentService
{
    private readonly HttpClient _http;
    private readonly KimiAgentOptions _options;
    private readonly ILogger<KimiAgentService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly IIncertezaDetectorService _incertezaDetector;
    private readonly ISessionContext _sessionContext;
    private readonly string _systemPrompt;

    public KimiAgentService(
        IOptions<KimiAgentOptions> options,
        PromptLoader prompts,
        IHttpClientFactory httpClientFactory,
        ILogger<KimiAgentService> logger,
        IContextoTelemetria contextoTelemetria,
        IIncertezaDetectorService incertezaDetector,
        ISessionContext sessionContext)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _incertezaDetector = incertezaDetector;
        _sessionContext = sessionContext;
        _http = httpClientFactory.CreateClient("kimi");
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

            // reasoning_content só entra quando ESTE turno foi respondido pelo próprio Kimi
            // (turnos de Claude/Grok/OpenAI nunca têm ReasoningContent) — omitir o campo
            // inteiramente pros demais, em vez de mandar null, evita qualquer ambiguidade do
            // lado da API sobre "sem raciocínio" vs. "raciocínio vazio".
            if (turno.ReasoningContent is { } raciocinio)
                mensagens.Add(new { role = "assistant", content = turno.Resposta ?? string.Empty, reasoning_content = raciocinio });
            else
                mensagens.Add(new { role = "assistant", content = turno.Resposta ?? string.Empty });
        }

        var userMessage = $"""
            Contexto das aulas:
            {contexto}

            Pergunta do aluno:
            {turnoAtual.Pergunta}
            """;
        mensagens.Add(new { role = "user", content = userMessage });

        // Sem temperature/top_p: sampling é fixo do lado do provedor (platform.kimi.ai/docs/
        // guide/kimi-k3-quickstart) — mandar esses campos não teria efeito, então nem envia.
        // max_completion_tokens (não max_tokens) é o nome de campo específico do Kimi.
        var payload = new { model = _options.Model, max_completion_tokens = _options.MaxTokens, messages = mensagens };
        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("v1/chat/completions", content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new RateLimitExcedidoException("Kimi.Agent");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Kimi agent falhou: {response.StatusCode} — {json}", inner: null, statusCode: response.StatusCode);

        var result = JsonConvert.DeserializeAnonymousType(json, new
        {
            choices = new[] { new { message = new { content = "", reasoning_content = (string?)null }, finish_reason = "" } },
            usage = new { prompt_tokens = 0, completion_tokens = 0 }
        });

        var texto = result?.choices?.FirstOrDefault()?.message.content;
        var raciocinioResposta = result?.choices?.FirstOrDefault()?.message.reasoning_content;

        _contextoTelemetria.Registrar(
            provedor: "Kimi",
            modelo: _options.Model,
            tokensEntrada: result?.usage?.prompt_tokens,
            tokensSaida: result?.usage?.completion_tokens,
            custoEstimadoUsd: PricingCalculator.Calcular("Kimi", _options.Model, result?.usage?.prompt_tokens, result?.usage?.completion_tokens),
            detalhes: new { finishReason = result?.choices?.FirstOrDefault()?.finish_reason, incerteza = texto != null && _incertezaDetector.ContemIncerteza(texto), temRaciocinio = raciocinioResposta != null },
            requestIdExterno: requestId);

        if (string.IsNullOrWhiteSpace(texto))
        {
            // Lança em vez de devolver uma resposta genérica — dentro da cadeia de fallback,
            // "sem texto" precisa contar como falha pra passar pro próximo provider, não virar
            // a resposta final silenciosamente.
            _logger.LogWarning("Kimi não retornou texto para {ChatId} — pergunta: {Pergunta}", chatId, turnoAtual.Pergunta);
            throw new InvalidOperationException("Kimi não retornou texto");
        }

        return new RespostaAgente(texto, raciocinioResposta);
    }
}
