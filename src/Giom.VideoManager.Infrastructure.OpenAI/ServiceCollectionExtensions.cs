using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.OpenAI;

public static class ServiceCollectionExtensions
{
    private sealed class OpenAiHttpClientMarker;

    /// <summary>
    /// Idempotente — chamado tanto por AddDirectEmbedding quanto por AddOpenAiSummary/
    /// AddOpenAiCategorizacao (Feature #101), que podem coexistir sem colisão de registro.
    /// </summary>
    private static IServiceCollection AddOpenAiHttpClient(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(OpenAiHttpClientMarker)))
            return services;

        services.AddSingleton<OpenAiHttpClientMarker>();

        services.AddHttpClient("openai", client =>
        {
            client.BaseAddress = new Uri("https://api.openai.com/");
            client.Timeout = TimeSpan.FromMinutes(5);
        }).AddStandardResilienceHandler();

        return services;
    }

    public static IServiceCollection AddDirectEmbedding(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DirectEmbeddingOptions>(config.GetSection("DirectEmbedding"));
        services.AddOpenAiHttpClient();
        services.AddHttpClient("supabase-functions").AddStandardResilienceHandler();
        services.AddContextoTelemetria();
        services.AddSingleton<IEmbeddingService, DirectEmbeddingService>();
        return services;
    }

    /// <summary>Registra o tipo concreto (não a interface) — a Composition decide como este elo entra na cadeia de fallback (Feature #101).</summary>
    public static IServiceCollection AddOpenAiSummary(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddOpenAiHttpClient();
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<OpenAiResumoOptions>(config.GetSection("OpenAiResumo"));
        services.AddSingleton<OpenAiResumoService>();
        return services;
    }

    public static IServiceCollection AddOpenAiCategorizacao(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddOpenAiHttpClient();
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<OpenAiCategorizacaoOptions>(config.GetSection("OpenAiCategorizacao"));
        services.AddSingleton<OpenAiCategorizacaoService>();
        return services;
    }
}
