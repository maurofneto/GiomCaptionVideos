using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.OpenAI;

public static class ServiceCollectionExtensions
{
    private sealed class OpenAiHttpClientMarker;

    /// <summary>
    /// Idempotente — chamado tanto por AddOpenAiEmbedding quanto por AddOpenAiSummary/
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
        }).AddStandardResilienceHandler(options => options.RegistrarEventoDeRetryNoSpan("OpenAI"));

        return services;
    }

    /// <summary>
    /// Registra o tipo concreto (não a interface) — a Composition decide como este elo entra na
    /// cadeia de fallback (Feature #101). A busca semântica delega a IVideoChunkRepository
    /// (Domain) — este projeto nunca referencia Infrastructure.Supabase diretamente.
    /// </summary>
    public static IServiceCollection AddOpenAiEmbedding(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OpenAiEmbeddingOptions>(config.GetSection("OpenAiEmbedding"));
        services.AddOpenAiHttpClient();
        services.AddContextoTelemetria();
        services.AddSingleton<OpenAIEmbeddingService>();
        return services;
    }

    /// <summary>Registra o tipo concreto (não a interface) — a Composition decide como este elo entra na cadeia de fallback (Feature #101).</summary>
    public static IServiceCollection AddOpenAiAgent(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddOpenAiHttpClient();
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<OpenAiAgentOptions>(config.GetSection("OpenAiAgent"));
        services.AddSingleton<OpenAiAgentService>();
        return services;
    }

    public static IServiceCollection AddOpenAiSummary(this IServiceCollection services, IConfiguration config)
    {
        services.AddOpenAiHttpClient();
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<OpenAiResumoOptions>(config.GetSection("OpenAiResumo"));
        services.AddSingleton<OpenAiResumoService>();
        return services;
    }

    public static IServiceCollection AddOpenAiCategorizacao(this IServiceCollection services, IConfiguration config)
    {
        services.AddOpenAiHttpClient();
        services.AddContextoTelemetria();
        services.AddSessionContext();
        services.Configure<OpenAiCategorizacaoOptions>(config.GetSection("OpenAiCategorizacao"));
        services.AddSingleton<OpenAiCategorizacaoService>();
        return services;
    }
}
