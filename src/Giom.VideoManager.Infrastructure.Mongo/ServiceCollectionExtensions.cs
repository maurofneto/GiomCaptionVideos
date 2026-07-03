using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.Mongo;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMongoPersistence(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MongoOptions>(config.GetSection("MongoDB"));
        services.AddSingleton<IVideoRepository, MongoVideoRepository>();
        services.AddSingleton<IProgressoRepository, MongoProgressoRepository>();
        return services;
    }
}
