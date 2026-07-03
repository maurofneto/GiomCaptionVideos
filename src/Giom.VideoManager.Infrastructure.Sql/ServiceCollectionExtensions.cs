using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.Sql;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlPersistence(this IServiceCollection services)
    {
        services.AddSingleton<IVideoRepository, SqlVideoRepository>();
        services.AddSingleton<IProgressoRepository, SqlProgressoRepository>();
        return services;
    }
}
