using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.SummarizeTech;

public static class ServiceCollectionExtensions
{
    /// <summary>Registra o tipo concreto (não a interface) — a Composition decide como este elo entra na cadeia de fallback (Feature #101).</summary>
    public static IServiceCollection AddSummarizeTechSummary(this IServiceCollection services)
    {
        services.AddSingleton<SummarizeTechResumoService>();
        return services;
    }
}
