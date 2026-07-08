using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.Voyage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVoyageEmbedding(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<VoyageEmbeddingOptions>(config.GetSection("VoyageEmbedding"));

        services.AddHttpClient("voyage", client =>
        {
            client.BaseAddress = new Uri("https://api.voyageai.com/");
            client.Timeout = TimeSpan.FromMinutes(5);
        }).AddStandardResilienceHandler();

        services.AddContextoTelemetria();
        services.AddSingleton<IEmbeddingService, VoyageEmbeddingService>();
        return services;
    }
}
