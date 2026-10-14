using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.Kimi;

public static class ServiceCollectionExtensions
{
    private sealed class KimiHttpClientMarker;

    private static IServiceCollection AddKimiHttpClient(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(KimiHttpClientMarker)))
            return services;

        services.AddSingleton<KimiHttpClientMarker>();

        // "Thinking" sempre ligado (platform.kimi.ai/docs/guide/kimi-k3-quickstart) — latência
        // de primeiro token maior que os outros provedores, por isso o mesmo timeout folgado do
        // GrokHttpClient (90s por tentativa, 5min total).
        services.AddHttpClient("kimi", client =>
        {
            client.BaseAddress = new Uri("https://api.moonshot.ai/");
            client.Timeout = TimeSpan.FromMinutes(5);
        }).AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(200);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            options.RegistrarEventoDeRetryNoSpan("Kimi");
        });

        return services;
    }

    /// <summary>Registra o tipo concreto (não a interface) — a Composition decide como este elo entra na cadeia de fallback (US #124).</summary>
    public static IServiceCollection AddKimiSummary(this IServiceCollection services, IConfiguration config)
    {
        services.AddKimiHttpClient();
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<KimiResumoOptions>(config.GetSection("KimiResumo"));
        services.AddSingleton<KimiResumoService>();
        return services;
    }

    public static IServiceCollection AddKimiCategorizacao(this IServiceCollection services, IConfiguration config)
    {
        services.AddKimiHttpClient();
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<KimiCategorizacaoOptions>(config.GetSection("KimiCategorizacao"));
        services.AddSingleton<KimiCategorizacaoService>();
        return services;
    }

    /// <summary>US #126 — mesmo mecanismo de AddKimiSummary/AddKimiCategorizacao.</summary>
    public static IServiceCollection AddKimiAgent(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddKimiHttpClient();
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.AddOptions<KimiAgentOptions>()
            .Bind(config.GetSection("KimiAgent"))
            .Validate(o => o.MaxTokens > 0, "KimiAgent:MaxTokens deve ser maior que zero (padrão 4096 — remova a chave pra usar o padrão).")
            .ValidateOnStart();
        services.AddSingleton<KimiAgentService>();
        return services;
    }
}
