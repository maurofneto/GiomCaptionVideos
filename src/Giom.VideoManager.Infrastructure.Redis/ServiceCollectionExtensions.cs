using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Giom.VideoManager.Infrastructure.Redis;

public static class ServiceCollectionExtensions
{
    /// <summary>Decora o IEtapaProvedorConfigService já registrado com cache Redis (US #106) — ver RedisCacheEtapaProvedorConfigService.</summary>
    public static IServiceCollection AddRedisCacheEtapaProvedorConfig(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RedisCacheOptions>(config.GetSection("EtapaProvedorConfig:Cache:Redis"));
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
            return ConnectionMultiplexer.Connect(options.ConnectionString);
        });
        services.Decorate<IEtapaProvedorConfigService, RedisCacheEtapaProvedorConfigService>();
        return services;
    }
}
