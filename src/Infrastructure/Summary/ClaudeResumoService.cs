using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Domain.Entities;
using Domain.Interfaces;
using Infrastructure.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Summary;

public class ClaudeResumoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
}

/// <summary>
/// Gera resumo executivo usando Claude Haiku.
/// System prompt carregado de Prompts/resumo.md e cacheado via PromptCaching.
/// Usa Transcricao.ConteudoLimpo (sem timestamps) como entrada.
/// </summary>
public class ClaudeResumoService : IResumoService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeResumoOptions _options;
    private readonly ILogger<ClaudeResumoService> _logger;
    private readonly string _systemPrompt;

    public ClaudeResumoService(IOptions<ClaudeResumoOptions> options, PromptLoader prompts, IHttpClientFactory httpClientFactory, ILogger<ClaudeResumoService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new AnthropicClient(_options.ApiKey, httpClientFactory.CreateClient("anthropic"));
        _systemPrompt = prompts.Load("resumo.md");
    }

    public async Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
    {
        var textoLimpo = video.Transcricao?.ConteudoLimpo;
        if (string.IsNullOrWhiteSpace(textoLimpo))
            return null;

        try
        {
            var response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model         = _options.Model,
                MaxTokens     = 2048,
                PromptCaching = PromptCacheType.AutomaticToolsAndSystem,
                System        = [new SystemMessage(_systemPrompt)],
                Messages      = [new Message { Role = RoleType.User, Content = [new TextContent { Text = textoLimpo }] }]
            }, ct);

            var content = response.Content.OfType<TextContent>().FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(content))
                return null;

            _logger.LogInformation("Resumo gerado para {VideoId}", video.Id);

            return new Resumo
            {
                VideoId   = video.Id,
                Descricao = content,
                Origem    = "Claude",
                GeradoEm  = DateTime.UtcNow,
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
