using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Anthropic;

public class ClaudeResumoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-4-5-20251001";

    // O prompt pede ~1200-1500 palavras — em português isso passa fácil de 2048 tokens; deixado
    // com folga pra não cortar o resumo no meio. Default usado quando o canal (US #106) não
    // fixa Model/MaxTokens próprios em EtapaProvedorConfig.
    public int MaxTokens { get; init; } = 4096;
}

/// <summary>
/// Gera resumo executivo usando Claude. Prompt e Model/MaxTokens vêm de
/// <see cref="EtapaProvedorConfig"/>, resolvida por canal (US #106) — este serviço não decide
/// fallback nem cacheia config, só executa UMA chamada com o que recebeu (ver
/// FallbackResumoService, único IResumoService público). Não sabe nem precisa saber se a chamada
/// vai passar por observabilidade externa (Helicone) — isso é decidido antes (FallbackResumoService
/// estampa EtapaProvedorConfig.UsarObservabilidade no ISessionContext ambiente) e resolvido depois,
/// no transporte HTTP (ver HeliconeRoteamentoHandler em Infrastructure.Helicone), sem o client
/// aqui precisar saber qual URL/headers isso implica.
/// Usa Transcricao.ConteudoLimpo (sem timestamps) como entrada.
/// Telemetria (tempo/sucesso/falha) é responsabilidade do decorator TelemetriaResumoService —
/// aqui só contribuímos os metadados ricos (provedor/modelo/tokens) via IContextoTelemetria.
/// </summary>
public class ClaudeResumoService : IResumoProvedorService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeResumoOptions _options;
    private readonly ILogger<ClaudeResumoService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly ISessionContext _sessionContext;
    private readonly ITracingService _tracingService;
    private readonly IPricingService _pricingService;

    public ClaudeResumoService(IOptions<ClaudeResumoOptions> options, IHttpClientFactory httpClientFactory, ILogger<ClaudeResumoService> logger, IContextoTelemetria contextoTelemetria, ISessionContext sessionContext, ITracingService tracingService, IPricingService pricingService)
    {
        _options = options.Value;
        _logger = logger;
        _contextoTelemetria = contextoTelemetria;
        _sessionContext = sessionContext;
        _tracingService = tracingService;
        _pricingService = pricingService;
        _client = new AnthropicClient(_options.ApiKey, httpClientFactory.CreateClient("anthropic"));
    }

    public async Task<Resumo?> GerarResumoAsync(Video video, EtapaProvedorConfig config, CancellationToken ct = default)
    {
        var textoLimpo = video.Transcricao?.ConteudoLimpo;
        if (string.IsNullOrWhiteSpace(textoLimpo))
            return null;

        _sessionContext.Definir(_sessionContext.Atual.ParaEtapa("resumo", video));
        using var span = _tracingService.IniciarEtapaAtual("resumo");

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
                Messages      = [new Message { Role = RoleType.User, Content = [new TextContent { Text = textoLimpo }] }]
            }, ct);
        }
        catch (RateLimitsExceeded ex)
        {
            // Anthropic informa quando os limites resetam (RateLimits.RetryAfter) — repassado
            // pro Domain pra o consumer da fila (US #44) reagendar de acordo, em vez de retry rápido.
            throw new RateLimitExcedidoException("Claude.Resumo", ex.RateLimits.RetryAfter, ex);
        }

        // Concatena todos os blocos de texto — FirstOrDefault() descartava o resto
        // quando a resposta vinha em mais de um bloco.
        var content = string.Concat(response.Content.OfType<TextContent>().Select(c => c.Text));

        _contextoTelemetria.Registrar(
            provedor: "Claude",
            modelo: modelo,
            tokensEntrada: response.Usage?.InputTokens,
            tokensSaida: response.Usage?.OutputTokens,
            custoEstimadoUsd: await _pricingService.CalcularAsync("Claude", modelo, response.Usage?.InputTokens, response.Usage?.OutputTokens, ct),
            detalhes: new { stopReason = response.StopReason });

        if (string.IsNullOrWhiteSpace(content))
            throw new NenhumResultadoEncontradoException("Claude.Resumo", $"resposta veio vazia pro vídeo {video.Id} (stopReason={response.StopReason})");

        if (response.StopReason == "max_tokens")
            _logger.LogWarning("Resumo cortado por MaxTokens ({MaxTokens}) para {VideoId}", maxTokens, video.Id);

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
