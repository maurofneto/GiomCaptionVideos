using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
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
    // Heurística — não é 100% precisa (o texto varia a cada geração), mas correlaciona bem
    // com as instruções da persona (agent.md) pra admitir incerteza e mencionar o Dr. Wagner.
    // Serve como proxy de "buraco de conteúdo", não como detecção exata.
    private static readonly string[] SinaisIncerteza =
    [
        "não sei", "não tenho certeza", "não consigo confirmar", "não consigo afirmar",
        "não vi registrad", "não tenho essa informação", "não encontrei isso nas aulas",
        "vale a pena perguntar", "procure o dr. wagner", "procurar o dr. wagner"
    ];

    private static bool ContemSinalDeIncerteza(string texto) =>
        SinaisIncerteza.Any(sinal => texto.Contains(sinal, StringComparison.OrdinalIgnoreCase));

    private readonly AnthropicClient _client;
    private readonly ClaudeAgentOptions _options;
    private readonly ILogger<ClaudeAgentService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly string _systemPrompt;

    public ClaudeAgentService(
        IOptions<ClaudeAgentOptions> options,
        IOptions<HeliconeOptions> heliconeOptions,
        PromptLoader prompts,
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeAgentService> logger,
        IContextoTelemetria contextoTelemetria)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _client = AnthropicClientFactory.Create(_options.ApiKey, httpClientFactory.CreateClient("anthropic"), _options.UseHelicone, heliconeOptions.Value);
        _systemPrompt = prompts.Load("agent.md");
    }

    public async Task<string> ResponderAsync(string chatId, TurnoConversa turnoAtual, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default)
    {
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
            custoEstimadoUsd: AnthropicPricingCalculator.Calcular(_options.Model, response.Usage?.InputTokens, response.Usage?.OutputTokens),
            detalhes: new { stopReason = response.StopReason, incerteza = ContemSinalDeIncerteza(texto) });

        if (string.IsNullOrWhiteSpace(texto))
        {
            _logger.LogWarning("Claude não retornou texto para {ChatId} — pergunta: {Pergunta}", chatId, turnoAtual.Pergunta);
            return "Desculpe, não consegui gerar uma resposta agora. Tente novamente em instantes.";
        }

        return texto;
    }
}
