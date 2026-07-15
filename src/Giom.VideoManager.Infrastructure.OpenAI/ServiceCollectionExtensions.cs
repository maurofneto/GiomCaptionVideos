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

    /// <summary>
    /// Registra o tipo concreto (não a interface) — a Composition decide como este elo entra na
    /// cadeia de fallback (Feature #101). A seção de config continua "DirectEmbedding" (não foi
    /// renomeada junto com a classe) — o SSM já tem /giom/{env}/{app}/DirectEmbedding/OpenAiApiKey
    /// provisionado, e renomear a seção quebraria esses secrets.
    /// </summary>
    public static IServiceCollection AddOpenAiEmbedding(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DirectEmbeddingOptions>(config.GetSection("DirectEmbedding"));
        services.AddOpenAiHttpClient();
        services.AddHttpClient("supabase-functions").AddStandardResilienceHandler();
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
