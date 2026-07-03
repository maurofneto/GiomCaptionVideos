using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPromptLoader(this IServiceCollection services)
    {
        services.AddSingleton<PromptLoader>();
        return services;
    }
}
