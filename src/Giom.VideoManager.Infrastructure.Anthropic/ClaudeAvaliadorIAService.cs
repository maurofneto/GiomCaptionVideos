using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Helicone;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Anthropic;

public class ClaudeAvaliadorIAOptions
{
    public string ApiKey { get; init; } = string.Empty;

    // Classificação binária simples — Haiku é suficiente e bem mais barato que o modelo
    // usado pra gerar a resposta de verdade.
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; init; } = 256;

    /// <summary>Roteia as chamadas pelo gateway do Helicone — requer Helicone:ApiKey configurada.</summary>
    public bool UseHelicone { get; init; } = false;
}

/// <summary>
/// Implementação Claude de IAvaliadorIAService — única hoje, mas plugável (ver
/// AddAvaliadorIAService na Composition). A instrução (o que aprovar/reprovar) vem de fora
/// (ver GuardRailIAOptions), então esta classe não conhece regra nenhuma de negócio, só sabe
/// pedir pro modelo avaliar e interpretar o JSON de volta.
/// </summary>
public class ClaudeAvaliadorIAService : IAvaliadorIAService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeAvaliadorIAOptions _options;
    private readonly ILogger<ClaudeAvaliadorIAService> _logger;
    private readonly ITelemetriaService _telemetriaService;
    private readonly ISessionContext _sessionContext;

    public ClaudeAvaliadorIAService(
        IOptions<ClaudeAvaliadorIAOptions> options,
        IOptions<HeliconeOptions> heliconeOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeAvaliadorIAService> logger,
        ITelemetriaService telemetriaService,
        ISessionContext sessionContext)
    {
        _options = options.Value;
        _logger = logger;
        _telemetriaService = telemetriaService;
        _sessionContext = sessionContext;
        _client = AnthropicClientFactory.Create(_options.ApiKey, httpClientFactory.CreateClient("anthropic"), _options.UseHelicone, heliconeOptions.Value);
    }

    public async Task<AvaliacaoIA> AvaliarAsync(string instrucao, string conteudo, CancellationToken ct = default)
    {
        _sessionContext.Definir(new SessaoObservabilidade(
            SessionId: Guid.NewGuid().ToString(),
            SessionName: "GuardRail IA",
            SessionPath: "/guardrail/avaliacao-ia",
            Propriedades: new Dictionary<string, string> { ["Tipo"] = "guardrail_ia" }));

        var systemPrompt = $$"""
            {{instrucao}}

            Responda ESTRITAMENTE em JSON, sem nenhum texto antes ou depois:
            {"aprovado": true|false, "motivo": "..."}
            """;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        AvaliacaoIA resultado;
        bool sucesso;
        int? tokensEntrada = null, tokensSaida = null;
        decimal? custo = null;

        try
        {
            var response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model     = _options.Model,
                MaxTokens = _options.MaxTokens,
                System    = [new SystemMessage(systemPrompt)],
                Messages  = [new Message { Role = RoleType.User, Content = [new TextContent { Text = conteudo }] }]
            }, ct);

            var json = string.Concat(response.Content.OfType<TextContent>().Select(c => c.Text))
                .Replace("```json", "").Replace("```", "").Trim();

            tokensEntrada = response.Usage?.InputTokens;
            tokensSaida = response.Usage?.OutputTokens;
            custo = AnthropicPricingCalculator.Calcular(_options.Model, tokensEntrada, tokensSaida);

            var parseado = JsonConvert.DeserializeAnonymousType(json, new { aprovado = true, motivo = (string?)null });
            resultado = new AvaliacaoIA(parseado?.aprovado ?? true, parseado?.motivo);
            sucesso = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Falha do avaliador (erro de rede, JSON inválido, etc.) nunca deveria bloquear um
            // usuário legítimo — fail-open. Quem quer bloquear de verdade usa um guardrail
            // determinístico (PalavrasBloqueadas, PromptInjection) em paralelo.
            _logger.LogWarning(ex, "Avaliador de IA falhou — aprovando por padrão (fail-open)");
            resultado = new AvaliacaoIA(true);
            sucesso = false;
        }

        // Chamado direto (não por um decorator Telemetria* pareado com IContextoTelemetria) —
        // por isso registra na ITelemetriaService diretamente. Sem um chatId/turnoId genérico
        // disponível nesta interface, o EntidadeId fica um valor fixo — correlação mais fraca
        // que os outros registros, mas ainda dá visibilidade de custo/tempo no Sentry/Supabase.
        await _telemetriaService.RegistrarAsync(new RegistroTelemetria(
            Tipo: "guardrail_ia",
            EntidadeId: "guardrail",
            TempoMs: stopwatch.ElapsedMilliseconds,
            Sucesso: sucesso,
            Provedor: "Claude",
            Modelo: _options.Model,
            TokensEntrada: tokensEntrada,
            TokensSaida: tokensSaida,
            CustoEstimadoUsd: custo
        ), ct);

        return resultado;
    }
}
