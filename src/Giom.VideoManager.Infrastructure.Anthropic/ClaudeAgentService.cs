using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Helicone;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Anthropic;

public class ClaudeAgentOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; init; } = 4096;

    /// <summary>Roteia as chamadas pelo gateway do Helicone (custo/latência/tokens por chamada) — requer Helicone:ApiKey configurada.</summary>
    public bool UseHelicone { get; init; } = false;
}

/// <summary>
/// Agent de perguntas e respostas: gera a resposta com Claude a partir da pergunta e do
/// contexto já recuperado por quem orquestra (ChatBotProcessor). Não faz busca semântica
/// nem conhece canal.
/// </summary>
public class ClaudeAgentService : IAgentService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeAgentOptions _options;
    private readonly ILogger<ClaudeAgentService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly IIncertezaDetectorService _incertezaDetector;
    private readonly ISessionContext _sessionContext;
    private readonly string _systemPrompt;
    private readonly IPricingService _pricingService;

    public ClaudeAgentService(
        IOptions<ClaudeAgentOptions> options,
        IOptions<HeliconeOptions> heliconeOptions,
        PromptLoader prompts,
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeAgentService> logger,
        IContextoTelemetria contextoTelemetria,
        IIncertezaDetectorService incertezaDetector,
        ISessionContext sessionContext,
        IPricingService pricingService)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _incertezaDetector = incertezaDetector;
        _sessionContext = sessionContext;
        _pricingService = pricingService;
        _client = AnthropicClientFactory.Create(_options.ApiKey, httpClientFactory.CreateClient("anthropic"), _options.UseHelicone, heliconeOptions.Value);
        _systemPrompt = prompts.Load("agent.md");
    }

    public async Task<RespostaAgente> ResponderAsync(string chatId, TurnoConversa turnoAtual, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default)
    {
        // RequestId sempre gerado — quem decide se algo realmente consome isso (ex.: reportar
        // feedback via Helicone) é a plataforma externa de observabilidade, não esta classe.
        var requestId = Guid.NewGuid().ToString();

        // ChatId como Session-Id — agrupa toda a conversa (todas as perguntas de um mesmo chat)
        // numa eventual plataforma de observabilidade externa; User-Id igual, pra métricas "Users".
        _sessionContext.Definir(new SessaoObservabilidade(
            SessionId: chatId,
            SessionName: "Bot Instituto Giom",
            SessionPath: "/bot/pergunta",
            UserId: chatId,
            Propriedades: new Dictionary<string, string> { ["Tipo"] = "bot_resposta" },
            RequestId: requestId,
            UsarObservabilidade: _options.UseHelicone));

        var mensagens = new List<Message>();

        foreach (var turno in historico)
        {
            mensagens.Add(new Message { Role = RoleType.User, Content = [new TextContent { Text = turno.Pergunta }] });
            mensagens.Add(new Message { Role = RoleType.Assistant, Content = [new TextContent { Text = turno.Resposta ?? string.Empty }] });
        }

        var userMessage = $"""
            Contexto das aulas:
            {contexto}

            Pergunta do aluno:
            {turnoAtual.Pergunta}
            """;
        mensagens.Add(new Message { Role = RoleType.User, Content = [new TextContent { Text = userMessage }] });

        var response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Model         = _options.Model,
            MaxTokens     = _options.MaxTokens,
            PromptCaching = PromptCacheType.AutomaticToolsAndSystem,
            System        = [new SystemMessage(_systemPrompt)],
            Messages      = mensagens
        }, ct);

        // Concatena TODOS os blocos de texto — FirstOrDefault() pegava só o primeiro e
        // descartava o resto quando a resposta vinha em mais de um bloco, cortando a
        // resposta no meio sem erro nenhum (o StopReason continuava "end_turn" normal).
        var texto = string.Concat(response.Content.OfType<TextContent>().Select(c => c.Text));

        if (response.StopReason == "max_tokens")
            _logger.LogWarning("Resposta cortada por MaxTokens ({MaxTokens}) para {ChatId} — pergunta: {Pergunta}", _options.MaxTokens, chatId, turnoAtual.Pergunta);

        _contextoTelemetria.Registrar(
            provedor: "Claude",
            modelo: _options.Model,
            tokensEntrada: response.Usage?.InputTokens,
            tokensSaida: response.Usage?.OutputTokens,
            custoEstimadoUsd: await _pricingService.CalcularAsync("Claude", _options.Model, response.Usage?.InputTokens, response.Usage?.OutputTokens, ct),
            detalhes: new { stopReason = response.StopReason, incerteza = _incertezaDetector.ContemIncerteza(texto) },
            requestIdExterno: requestId);

        if (string.IsNullOrWhiteSpace(texto))
        {
            // Lança em vez de devolver uma resposta genérica — dentro da cadeia de fallback
            // (Feature #101), "sem texto" precisa contar como falha pra passar pro próximo
            // provider, não virar a resposta final silenciosamente.
            _logger.LogWarning("Claude não retornou texto para {ChatId} — pergunta: {Pergunta}", chatId, turnoAtual.Pergunta);
            throw new InvalidOperationException("Claude não retornou texto");
        }

        return new RespostaAgente(texto);
    }
}
