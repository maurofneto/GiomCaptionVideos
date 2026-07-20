using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.Cohere;

public static class ServiceCollectionExtensions
{
    /// <summary>Registra o tipo concreto (não a interface) — a Composition decide como este elo entra na cadeia de fallback (Feature #101).</summary>
    public static IServiceCollection AddCohereEmbedding(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<CohereEmbeddingOptions>(config.GetSection("CohereEmbedding"));

        services.AddHttpClient("cohere", client =>
        {
            client.BaseAddress = new Uri("https://api.cohere.com/");
            client.Timeout = TimeSpan.FromMinutes(5);
        }).AddStandardResilienceHandler(options => options.RegistrarEventoDeRetryNoSpan("Cohere"));

        services.AddContextoTelemetria();
        services.AddSingleton<CohereEmbeddingService>();
        return services;
    }
}
