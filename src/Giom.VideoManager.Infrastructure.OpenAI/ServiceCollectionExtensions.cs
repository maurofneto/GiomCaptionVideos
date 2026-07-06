using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.OpenAI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDirectEmbedding(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DirectEmbeddingOptions>(config.GetSection("DirectEmbedding"));

        services.AddHttpClient("openai", client =>
        {
            client.BaseAddress = new Uri("https://api.openai.com/");
            client.Timeout = TimeSpan.FromMinutes(5);
        }).AddStandardResilienceHandler();

        services.AddHttpClient("supabase-functions").AddStandardResilienceHandler();

        services.AddContextoTelemetria();
        services.AddSingleton<DirectEmbeddingService>();
        return services;
    }
}
