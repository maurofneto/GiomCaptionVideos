using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.SummarizeTech;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSummarizeTechSummary(this IServiceCollection services)
    {
        services.AddSingleton<IResumoService, SummarizeTechResumoService>();
        return services;
    }
}
