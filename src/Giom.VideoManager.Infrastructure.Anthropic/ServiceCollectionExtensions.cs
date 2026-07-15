using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Helicone;

namespace Giom.VideoManager.Infrastructure.Anthropic;

public static class ServiceCollectionExtensions
{
    private sealed class AnthropicHttpClientMarker;

    private static IServiceCollection AddAnthropicHttpClient(this IServiceCollection services, IConfiguration config)
    {
        if (services.Any(d => d.ServiceType == typeof(AnthropicHttpClientMarker)))
            return services;

        services.AddSingleton<AnthropicHttpClientMarker>();
        // Helicone:ApiKey é compartilhada. ClaudeAgentService/ClaudeAvaliadorIAService (decisão
        // fixa no startup, via sua própria opção UseHelicone) ainda usam AnthropicClientFactory
        // pra rotear na construção do client — ver AnthropicClientFactory. ClaudeResumoService/
        // ClaudeCategorizacaoService (US #106) NÃO usam mais isso: a decisão varia por chamada
        // (config por canal), então o roteamento saiu daqui e virou responsabilidade do
        // transporte HTTP — Infrastructure.Helicone anexa um DelegatingHandler genérico a este
        // mesmo HttpClient "anthropic" (e também a "openai"/"grok") que lê o ISessionContext
        // ambiente por requisição, não mais fixado num client específico.
        services.Configure<HeliconeOptions>(config.GetSection("Helicone"));

        services.AddHttpClient("anthropic")
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(200);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            });

        return services;
    }

    // Cada Add* registra a implementação concreta na interface e decora com o decorator
    // genérico de telemetria via Scrutor (services.Decorate) — qualquer IAgentService/
    // IResumoService/ICategorizacaoService passa a registrar telemetria automaticamente, sem
    // "new" nenhum montando a decoração à mão (Scrutor resolve o "inner" como a implementação
    // já registrada antes do Decorate).

    /// <summary>Registra o tipo concreto (não a interface) — desde a Feature #101, a Composition decide como este elo entra na cadeia de fallback (Claude → Grok → OpenAI), decorando telemetria uma vez só sobre o resultado final.</summary>
    public static IServiceCollection AddClaudeAgent(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddAnthropicHttpClient(config);
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<ClaudeAgentOptions>(config.GetSection("ClaudeAgent"));
        services.AddSingleton<ClaudeAgentService>();
        return services;
    }

    /// <summary>
    /// Registra o tipo concreto (não a interface) — desde a Feature #101, quem decide como
    /// ClaudeResumoService vira o IResumoService final (direto, ou um elo de
    /// FallbackResumoService) é a Composition, não este método. Decoração de telemetria também
    /// migrou pra lá, aplicada uma vez só sobre o resultado final da cadeia.
    /// </summary>
    public static IServiceCollection AddClaudeSummary(this IServiceCollection services, IConfiguration config)
    {
        services.AddAnthropicHttpClient(config);
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<ClaudeResumoOptions>(config.GetSection("ClaudeResumo"));
        services.AddSingleton<ClaudeResumoService>();
        return services;
    }

    /// <summary>Ver comentário de AddClaudeSummary — mesmo raciocínio (tipo concreto, decisão de composição na Composition).</summary>
    public static IServiceCollection AddClaudeCategorizacao(this IServiceCollection services, IConfiguration config)
    {
        services.AddAnthropicHttpClient(config);
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<ClaudeCategorizacaoOptions>(config.GetSection("ClaudeCategorizacao"));
        services.AddSingleton<ClaudeCategorizacaoService>();
        return services;
    }

    /// <summary>
    /// Única implementação de IAvaliadorIAService hoje — usada pelo GuardRailIA (ver
    /// Infrastructure.GuardRails). Registra sua própria telemetria direto em ITelemetriaService
    /// (Tipo "guardrail_ia"), sem passar pelo par IContextoTelemetria/decorator Telemetria*.
    /// </summary>
    public static IServiceCollection AddClaudeAvaliadorIA(this IServiceCollection services, IConfiguration config)
    {
        services.AddAnthropicHttpClient(config);
        services.AddSessionContext();
        services.Configure<ClaudeAvaliadorIAOptions>(config.GetSection("ClaudeAvaliadorIA"));
        services.AddSingleton<IAvaliadorIAService, ClaudeAvaliadorIAService>();
        return services;
    }
}
