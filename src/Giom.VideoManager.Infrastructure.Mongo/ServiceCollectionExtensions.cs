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

    /// <summary>Histórico de conversa persistido — sobrevive a reinícios do worker.</summary>
    public static IServiceCollection AddMongoConversaHistorico(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MongoOptions>(config.GetSection("MongoDB"));
        services.AddSingleton<IConversaHistoricoService, MongoConversaHistoricoService>();
        return services;
    }
}
