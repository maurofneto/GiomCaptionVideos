using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Domain.Interfaces;
using Infrastructure.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Agent;

public class ClaudeAgentOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
}

/// <summary>
/// Agent de perguntas e respostas: busca contexto via IEmbeddingService (RAG) e gera
/// a resposta com Claude. Não conhece canal — quem chama decide de onde veio a pergunta
/// e para onde vai a resposta.
/// </summary>
public class ClaudeAgentService : IAgentService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeAgentOptions _options;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<ClaudeAgentService> _logger;
    private readonly string _systemPrompt;

    public ClaudeAgentService(
        IOptions<ClaudeAgentOptions> options,
        PromptLoader prompts,
        IHttpClientFactory httpClientFactory,
        IEmbeddingService embeddingService,
        ILogger<ClaudeAgentService> logger)
    {
        _options = options.Value;
        _embeddingService = embeddingService;
        _logger = logger;
        _client = new AnthropicClient(_options.ApiKey, httpClientFactory.CreateClient("anthropic"));
        _systemPrompt = prompts.Load("agent.md");
    }

    public async Task<string> PerguntarAsync(string pergunta, string? area = null, CancellationToken ct = default)
    {
        var busca = await _embeddingService.BuscarAsync(pergunta, area, ct: ct);

        var userMessage = $"""
            Contexto das aulas:
            {busca.ContextoFormatado}

            Pergunta do aluno:
            {pergunta}
            """;

        var response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Model         = _options.Model,
            MaxTokens     = 1536,
            PromptCaching = PromptCacheType.AutomaticToolsAndSystem,
            System        = [new SystemMessage(_systemPrompt)],
            Messages      = [new Message { Role = RoleType.User, Content = [new TextContent { Text = userMessage }] }]
        }, ct);

        var texto = response.Content.OfType<TextContent>().FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(texto))
        {
            _logger.LogWarning("Claude não retornou texto para a pergunta: {Pergunta}", pergunta);
            return "Desculpe, não consegui gerar uma resposta agora. Tente novamente em instantes.";
        }

        return texto;
    }
}
