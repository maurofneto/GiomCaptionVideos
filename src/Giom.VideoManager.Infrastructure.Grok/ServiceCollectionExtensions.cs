using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.Grok;

public static class ServiceCollectionExtensions
{
    private sealed class GrokHttpClientMarker;

    private static IServiceCollection AddGrokHttpClient(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(GrokHttpClientMarker)))
            return services;

        services.AddSingleton<GrokHttpClientMarker>();

        services.AddHttpClient("grok", client =>
        {
            client.BaseAddress = new Uri("https://api.x.ai/");
            client.Timeout = TimeSpan.FromMinutes(5);
        }).AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(200);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            options.RegistrarEventoDeRetryNoSpan("Grok");
        });

        return services;
    }

    /// <summary>Registra o tipo concreto (não a interface) — a Composition decide como este elo entra na cadeia de fallback (Feature #101).</summary>
    public static IServiceCollection AddGrokAgent(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddGrokHttpClient();
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.AddOptions<GrokAgentOptions>()
            .Bind(config.GetSection("GrokAgent"))
            .Validate(o => o.MaxTokens > 0, "GrokAgent:MaxTokens deve ser maior que zero (padrão 4096 — remova a chave pra usar o padrão).")
            .ValidateOnStart();
        services.AddSingleton<GrokAgentService>();
        return services;
    }

    public static IServiceCollection AddGrokSummary(this IServiceCollection services, IConfiguration config)
    {
        services.AddGrokHttpClient();
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<GrokResumoOptions>(config.GetSection("GrokResumo"));
        services.AddSingleton<GrokResumoService>();
        return services;
    }

    public static IServiceCollection AddGrokCategorizacao(this IServiceCollection services, IConfiguration config)
    {
        services.AddGrokHttpClient();
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<GrokCategorizacaoOptions>(config.GetSection("GrokCategorizacao"));
        services.AddSingleton<GrokCategorizacaoService>();
        return services;
    }
}
