using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Anthropic;

public class ClaudeCategorizacaoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; init; } = 512;
}

/// <summary>
/// Classifica o resumo em até 3 áreas da Teologia Sistemática usando Claude. Prompt e
/// Model/MaxTokens vêm de <see cref="EtapaProvedorConfig"/>, resolvida por canal (US #106) — ver
/// ClaudeResumoService pro raciocínio completo (mesmo padrão, incluindo por que este serviço não
/// sabe nada sobre observabilidade/Helicone).
/// Telemetria (tempo/sucesso/falha) é responsabilidade do decorator TelemetriaCategorizacaoService —
/// aqui só contribuímos os metadados ricos (provedor/modelo/tokens) via IContextoTelemetria.
/// </summary>
public class ClaudeCategorizacaoService : ICategorizacaoProvedorService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeCategorizacaoOptions _options;
    private readonly ILogger<ClaudeCategorizacaoService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly ISessionContext _sessionContext;
    private readonly ITracingService _tracingService;

    public ClaudeCategorizacaoService(IOptions<ClaudeCategorizacaoOptions> options, IHttpClientFactory httpClientFactory, ILogger<ClaudeCategorizacaoService> logger, IContextoTelemetria contextoTelemetria, ISessionContext sessionContext, ITracingService tracingService)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _sessionContext = sessionContext;
        _tracingService = tracingService;
        _client = new AnthropicClient(_options.ApiKey, httpClientFactory.CreateClient("anthropic"));
    }

    public async Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, EtapaProvedorConfig config, CancellationToken ct = default)
    {
        var resumo = video.Resumo?.Descricao ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resumo)) return [];

        _sessionContext.Definir(_sessionContext.Atual.ParaEtapa("categorizacao", video));
        using var span = _tracingService.IniciarEtapaAtual("categorizacao");

        var modelo = config.Model ?? _options.Model;
        var maxTokens = config.MaxTokens ?? _options.MaxTokens;

        MessageResponse response;
        try
        {
            response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model         = modelo,
                MaxTokens     = maxTokens,
                PromptCaching = PromptCacheType.AutomaticToolsAndSystem,
                System        = [new SystemMessage(config.Prompt)],
                Messages      = [new Message { Role = RoleType.User, Content = [new TextContent { Text = resumo }] }]
            }, ct);
        }
        catch (RateLimitsExceeded ex)
        {
            throw new RateLimitExcedidoException("Claude.Categorizacao", ex.RateLimits.RetryAfter, ex);
        }

        // Concatena todos os blocos de texto — FirstOrDefault() descartava o resto
        // quando a resposta vinha em mais de um bloco.
        var json = string.Concat(response.Content.OfType<TextContent>().Select(c => c.Text));

        _contextoTelemetria.Registrar(
            provedor: "Claude",
            modelo: modelo,
            tokensEntrada: response.Usage?.InputTokens,
            tokensSaida: response.Usage?.OutputTokens,
            custoEstimadoUsd: AnthropicPricingCalculator.Calcular(modelo, response.Usage?.InputTokens, response.Usage?.OutputTokens),
            detalhes: new { stopReason = response.StopReason });

        if (string.IsNullOrWhiteSpace(json)) return [];

        if (response.StopReason == "max_tokens")
            _logger.LogWarning("Categorização cortada por MaxTokens ({MaxTokens}) para {VideoId}", maxTokens, video.Id);

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
