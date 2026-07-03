using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Anthropic;

public class ClaudeResumoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; init; } = 2048;
}

/// <summary>
/// Gera resumo executivo usando Claude — modelo configurável via ClaudeResumo:Model,
/// independente do modelo usado pela categorização ou pelo bot.
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
                MaxTokens     = _options.MaxTokens,
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
