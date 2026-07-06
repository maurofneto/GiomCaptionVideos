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

    // O prompt pede ~1200-1500 palavras (resumo.md) — em português isso passa fácil de
    // 2048 tokens; deixado com folga pra não cortar o resumo no meio.
    public int MaxTokens { get; init; } = 4096;

    /// <summary>Roteia as chamadas pelo gateway do Helicone (custo/latência/tokens por chamada) — requer Helicone:ApiKey configurada.</summary>
    public bool UseHelicone { get; init; } = false;
}

/// <summary>
/// Gera resumo executivo usando Claude — modelo configurável via ClaudeResumo:Model,
/// independente do modelo usado pela categorização ou pelo bot.
/// System prompt carregado de Prompts/resumo.md e cacheado via PromptCaching.
/// Usa Transcricao.ConteudoLimpo (sem timestamps) como entrada.
/// Telemetria (tempo/sucesso/falha) é responsabilidade do decorator TelemetriaResumoService —
/// aqui só contribuímos os metadados ricos (provedor/modelo/tokens) via IContextoTelemetria.
/// </summary>
public class ClaudeResumoService : IResumoService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeResumoOptions _options;
    private readonly ILogger<ClaudeResumoService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly string _systemPrompt;

    public ClaudeResumoService(IOptions<ClaudeResumoOptions> options, IOptions<HeliconeOptions> heliconeOptions, PromptLoader prompts, IHttpClientFactory httpClientFactory, ILogger<ClaudeResumoService> logger, IContextoTelemetria contextoTelemetria)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _client = AnthropicClientFactory.Create(_options.ApiKey, httpClientFactory.CreateClient("anthropic"), _options.UseHelicone, heliconeOptions.Value);
        _systemPrompt = prompts.Load("resumo.md");
    }

    public async Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
    {
        var textoLimpo = video.Transcricao?.ConteudoLimpo;
        if (string.IsNullOrWhiteSpace(textoLimpo))
            return null;

        var response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Model         = _options.Model,
            MaxTokens     = _options.MaxTokens,
            PromptCaching = PromptCacheType.AutomaticToolsAndSystem,
            System        = [new SystemMessage(_systemPrompt)],
            Messages      = [new Message { Role = RoleType.User, Content = [new TextContent { Text = textoLimpo }] }]
        }, ct);

        // Concatena todos os blocos de texto — FirstOrDefault() descartava o resto
        // quando a resposta vinha em mais de um bloco.
        var content = string.Concat(response.Content.OfType<TextContent>().Select(c => c.Text));

        _contextoTelemetria.Registrar(
            provedor: "Claude",
            modelo: _options.Model,
            tokensEntrada: response.Usage?.InputTokens,
            tokensSaida: response.Usage?.OutputTokens,
            custoEstimadoUsd: AnthropicPricingCalculator.Calcular(_options.Model, response.Usage?.InputTokens, response.Usage?.OutputTokens),
            detalhes: new { stopReason = response.StopReason });

        if (string.IsNullOrWhiteSpace(content))
            return null;

        if (response.StopReason == "max_tokens")
            _logger.LogWarning("Resumo cortado por MaxTokens ({MaxTokens}) para {VideoId}", _options.MaxTokens, video.Id);

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
}
