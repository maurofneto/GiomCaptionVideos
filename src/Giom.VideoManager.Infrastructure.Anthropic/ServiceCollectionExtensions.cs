using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Helicone;
using Giom.VideoManager.Infrastructure.Telemetria;

namespace Giom.VideoManager.Infrastructure.Anthropic;

public static class ServiceCollectionExtensions
{
    private sealed class AnthropicHttpClientMarker;

    private static IServiceCollection AddAnthropicHttpClient(this IServiceCollection services, IConfiguration config)
    {
        if (services.Any(d => d.ServiceType == typeof(AnthropicHttpClientMarker)))
            return services;

        services.AddSingleton<AnthropicHttpClientMarker>();
        // Helicone:ApiKey é compartilhada — cada Claude*Service decide individualmente se
        // usa (via sua própria opção UseHelicone), ver AnthropicClientFactory. Só o roteamento
        // (URL/auth do gateway) mora aqui — Sessions/Users/Feedback são genéricos (ver
        // ISessionContext) e ficam no projeto Infrastructure.Helicone, que anexa seu próprio
        // handler a este mesmo HttpClient "anthropic" quando registrado pela Composition.
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

    // Cada Add* registra a implementação concreta e envolve com o decorator genérico de
    // telemetria (Giom.VideoManager.Infrastructure.Telemetria) — qualquer IAgentService/
    // IResumoService/ICategorizacaoService passa a registrar telemetria automaticamente,
    // sem precisar chamar ITelemetriaService na mão.

    public static IServiceCollection AddClaudeAgent(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddAnthropicHttpClient(config);
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<ClaudeAgentOptions>(config.GetSection("ClaudeAgent"));
        services.AddSingleton<ClaudeAgentService>();
        services.AddSingleton<IAgentService>(sp => new TelemetriaAgentService(
            sp.GetRequiredService<ClaudeAgentService>(),
            sp.GetRequiredService<ITelemetriaService>(),
            sp.GetRequiredService<ContextoTelemetria>()));
        return services;
    }

    public static IServiceCollection AddClaudeSummary(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddAnthropicHttpClient(config);
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<ClaudeResumoOptions>(config.GetSection("ClaudeResumo"));
        services.AddSingleton<ClaudeResumoService>();
        services.AddSingleton<IResumoService>(sp => new TelemetriaResumoService(
            sp.GetRequiredService<ClaudeResumoService>(),
            sp.GetRequiredService<ITelemetriaService>(),
            sp.GetRequiredService<ContextoTelemetria>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TelemetriaResumoService>>()));
        return services;
    }

    public static IServiceCollection AddClaudeCategorizacao(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddAnthropicHttpClient(config);
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<ClaudeCategorizacaoOptions>(config.GetSection("ClaudeCategorizacao"));
        services.AddSingleton<ClaudeCategorizacaoService>();
        services.AddSingleton<ICategorizacaoService>(sp => new TelemetriaCategorizacaoService(
            sp.GetRequiredService<ClaudeCategorizacaoService>(),
            sp.GetRequiredService<ITelemetriaService>(),
            sp.GetRequiredService<ContextoTelemetria>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TelemetriaCategorizacaoService>>()));
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
