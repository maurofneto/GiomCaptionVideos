using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.Supabase;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSupabasePersistence(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseOptions>(config.GetSection("Supabase"));
        services.AddHttpClient("supabase").AddStandardResilienceHandler();
        services.AddSingleton<IVideoRepository, SupabaseVideoRepository>();
        services.AddSingleton<IProgressoRepository, SupabaseProgressoRepository>();
        services.AddSingleton<IVideoChunkRepository, SupabaseVideoChunkRepository>();
        return services;
    }

    public static IServiceCollection AddSupabaseEmbedding(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseEmbeddingOptions>(config.GetSection("Supabase"));
        services.AddSingleton<IEmbeddingService, SupabaseEmbeddingService>();
        return services;
    }
}
