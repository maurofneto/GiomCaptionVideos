using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Domain.Entities;
using Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Summary;

public class ClaudeResumoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-sonnet-4-6";
    public string PromptTemplate { get; init; } = DefaultPrompt;

    private const string DefaultPrompt =
        """
        Você é um assistente especializado em análise de conteúdo.
        Analise a transcrição a seguir e gere um resumo estruturado com:
        1. TEMA PRINCIPAL
        2. PONTOS PRINCIPAIS (3-5 conceitos)
        3. VERSÍCULOS CHAVE (se houver)
        4. DEFINIÇÕES IMPORTANTES
        5. CONCLUSÃO

        Responda em português do Brasil.

        TRANSCRIÇÃO:
        {transcricao}
        """;
}

/// <summary>
/// Gera resumo executivo usando a API Claude (Anthropic).
/// </summary>
public class ClaudeResumoService : IResumoService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeResumoOptions _options;
    private readonly ILogger<ClaudeResumoService> _logger;

    public ClaudeResumoService(IOptions<ClaudeResumoOptions> options, ILogger<ClaudeResumoService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new AnthropicClient(_options.ApiKey);
    }

    public async Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
    {
        var transcricao = video.Transcricao?.Conteudo;
        if (string.IsNullOrWhiteSpace(transcricao))
            return null;

        var prompt = _options.PromptTemplate.Replace("{transcricao}", transcricao);

        try
        {
            var response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model = _options.Model,
                MaxTokens = 2048,
                Messages = [new Message { Role = RoleType.User, Content = [new TextContent { Text = prompt }] }]
            }, ct);

            var content = response.Content.OfType<TextContent>().FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(content))
                return null;

            return new Resumo
            {
                VideoId = video.Id,
                Descricao = content,
                Origem = "Claude",
                GeradoEm = DateTime.UtcNow,
                Capitulos = []
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao gerar resumo via Claude para vídeo {VideoId}", video.Id);
            throw;
        }
    }
}
