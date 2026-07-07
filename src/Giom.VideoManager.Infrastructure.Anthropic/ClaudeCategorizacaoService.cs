using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Helicone;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Anthropic;

public class ClaudeCategorizacaoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; init; } = 512;

    /// <summary>Roteia as chamadas pelo gateway do Helicone (custo/latência/tokens por chamada) — requer Helicone:ApiKey configurada.</summary>
    public bool UseHelicone { get; init; } = false;
}

/// <summary>
/// Classifica o resumo em até 3 áreas da Teologia Sistemática usando Claude — modelo
/// configurável via ClaudeCategorizacao:Model, independente do resumo ou do bot.
/// System prompt carregado de Prompts/categorizacao.md e cacheado via PromptCaching.
/// Telemetria (tempo/sucesso/falha) é responsabilidade do decorator TelemetriaCategorizacaoService —
/// aqui só contribuímos os metadados ricos (provedor/modelo/tokens) via IContextoTelemetria.
/// </summary>
public class ClaudeCategorizacaoService : ICategorizacaoService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeCategorizacaoOptions _options;
    private readonly ILogger<ClaudeCategorizacaoService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly ISessionContext _sessionContext;
    private readonly string _systemPrompt;

    public ClaudeCategorizacaoService(IOptions<ClaudeCategorizacaoOptions> options, IOptions<HeliconeOptions> heliconeOptions, PromptLoader prompts, IHttpClientFactory httpClientFactory, ILogger<ClaudeCategorizacaoService> logger, IContextoTelemetria contextoTelemetria, ISessionContext sessionContext)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _sessionContext = sessionContext;
        _client = AnthropicClientFactory.Create(_options.ApiKey, httpClientFactory.CreateClient("anthropic"), _options.UseHelicone, heliconeOptions.Value);
        _systemPrompt = prompts.Load("categorizacao.md");
    }

    public async Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, CancellationToken ct = default)
    {
        var resumo = video.Resumo?.Descricao ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resumo)) return [];

        _sessionContext.Definir(new SessaoObservabilidade(
            SessionId: video.Id,
            SessionName: video.Title,
            SessionPath: "/pipeline/categorizacao",
            Propriedades: new Dictionary<string, string> { ["Tipo"] = "categorizacao", ["Canal"] = video.ChannelTitle }));

        var response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Model         = _options.Model,
            MaxTokens     = _options.MaxTokens,
            PromptCaching = PromptCacheType.AutomaticToolsAndSystem,
            System        = [new SystemMessage(_systemPrompt)],
            Messages      = [new Message { Role = RoleType.User, Content = [new TextContent { Text = resumo }] }]
        }, ct);

        // Concatena todos os blocos de texto — FirstOrDefault() descartava o resto
        // quando a resposta vinha em mais de um bloco.
        var json = string.Concat(response.Content.OfType<TextContent>().Select(c => c.Text));

        _contextoTelemetria.Registrar(
            provedor: "Claude",
            modelo: _options.Model,
            tokensEntrada: response.Usage?.InputTokens,
            tokensSaida: response.Usage?.OutputTokens,
            custoEstimadoUsd: AnthropicPricingCalculator.Calcular(_options.Model, response.Usage?.InputTokens, response.Usage?.OutputTokens),
            detalhes: new { stopReason = response.StopReason });

        if (string.IsNullOrWhiteSpace(json)) return [];

        if (response.StopReason == "max_tokens")
            _logger.LogWarning("Categorização cortada por MaxTokens ({MaxTokens}) para {VideoId}", _options.MaxTokens, video.Id);

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
}
