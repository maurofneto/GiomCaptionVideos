using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Anthropic;

public class ClaudeCategorizacaoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
}

/// <summary>
/// Classifica o resumo em até 3 áreas da Teologia Sistemática usando Claude Haiku.
/// System prompt carregado de Prompts/categorizacao.md e cacheado via PromptCaching.
/// </summary>
public class ClaudeCategorizacaoService : ICategorizacaoService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeCategorizacaoOptions _options;
    private readonly ILogger<ClaudeCategorizacaoService> _logger;
    private readonly string _systemPrompt;

    public ClaudeCategorizacaoService(IOptions<ClaudeCategorizacaoOptions> options, PromptLoader prompts, IHttpClientFactory httpClientFactory, ILogger<ClaudeCategorizacaoService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new AnthropicClient(_options.ApiKey, httpClientFactory.CreateClient("anthropic"));
        _systemPrompt = prompts.Load("categorizacao.md");
    }

    public async Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, CancellationToken ct = default)
    {
        var resumo = video.Resumo?.Descricao ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resumo)) return [];

        try
        {
            var response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model         = _options.Model,
                MaxTokens     = 512,
                PromptCaching = PromptCacheType.AutomaticToolsAndSystem,
                System        = [new SystemMessage(_systemPrompt)],
                Messages      = [new Message { Role = RoleType.User, Content = [new TextContent { Text = resumo }] }]
            }, ct);

            var json = response.Content.OfType<TextContent>().FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(json)) return [];

            _logger.LogInformation("Categorização gerada para {VideoId}", video.Id);

            json = json.Replace("\n", "").Replace("\r", "").Replace("```json", "").Replace("```", "").Trim();

            var items = JsonConvert.DeserializeAnonymousType(json,
                new[] { new { area = "", subcategoria = "", peso = 0.0 } });

            if (items == null || items.Length == 0) return [];

            var categorias = new List<CategoriaTeologica>();
            foreach (var item in items)
            {
                if (!Enum.TryParse<AreaTeologica>(item.area, ignoreCase: true, out var area))
                {
                    _logger.LogWarning("Área teológica desconhecida: {Area}", item.area);
                    continue;
                }

                categorias.Add(new CategoriaTeologica
                {
                    VideoId        = video.Id,
                    Area           = area,
                    Subcategoria   = item.subcategoria ?? string.Empty,
                    Peso           = (decimal)Math.Clamp(item.peso, 0.01, 1.0),
                    ClassificadoEm = DateTime.UtcNow
                });
            }

            return categorias.OrderByDescending(c => c.Peso).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao classificar vídeo {VideoId}", video.Id);
            throw;
        }
    }
}
