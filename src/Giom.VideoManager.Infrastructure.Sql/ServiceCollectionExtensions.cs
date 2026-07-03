using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.Sql;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlPersistence(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SqlOptions>(options =>
            options.ConnectionString = config.GetConnectionString("Default") ?? string.Empty);
        services.AddSingleton<IVideoRepository, SqlVideoRepository>();
        services.AddSingleton<IProgressoRepository, SqlProgressoRepository>();
        return services;
    }
}
