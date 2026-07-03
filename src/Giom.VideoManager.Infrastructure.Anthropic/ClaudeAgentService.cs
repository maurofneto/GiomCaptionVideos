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
    private readonly string _systemPrompt;

    public ClaudeAgentService(
        IOptions<ClaudeAgentOptions> options,
        PromptLoader prompts,
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeAgentService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new AnthropicClient(_options.ApiKey, httpClientFactory.CreateClient("anthropic"));
        _systemPrompt = prompts.Load("agent.md");
    }

    public async Task<string> ResponderAsync(string pergunta, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default)
    {
        var mensagens = new List<Message>();

        foreach (var turno in historico)
        {
            mensagens.Add(new Message { Role = RoleType.User, Content = [new TextContent { Text = turno.Pergunta }] });
            mensagens.Add(new Message { Role = RoleType.Assistant, Content = [new TextContent { Text = turno.Resposta }] });
        }

        var userMessage = $"""
            Contexto das aulas:
            {contexto}

            Pergunta do aluno:
            {pergunta}
            """;
        mensagens.Add(new Message { Role = RoleType.User, Content = [new TextContent { Text = userMessage }] });

        var response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Model         = _options.Model,
            MaxTokens     = 1536,
            PromptCaching = PromptCacheType.AutomaticToolsAndSystem,
            System        = [new SystemMessage(_systemPrompt)],
            Messages      = mensagens
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
