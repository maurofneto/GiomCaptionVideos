using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Incerteza;
using Giom.VideoManager.Infrastructure.Telemetria;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPromptLoader(this IServiceCollection services)
    {
        services.AddSingleton<PromptLoader>();
        return services;
    }

    /// <summary>Única implementação hoje — heurística de palavras-chave, sem dependência externa.</summary>
    public static IServiceCollection AddHeuristicaIncertezaDetector(this IServiceCollection services)
    {
        services.AddSingleton<IIncertezaDetectorService, HeuristicaIncertezaDetectorService>();
        return services;
    }

    /// <summary>
    /// Registra o canal de metadados de telemetria (AsyncLocal) usado pelos decorators
    /// genéricos TelemetriaResumoService/TelemetriaCategorizacaoService/TelemetriaAgentService.
    /// Idempotente — pode ser chamado por múltiplos Add* na mesma composição (ex.: um worker
    /// que registra tanto resumo quanto categorização).
    /// </summary>
    public static IServiceCollection AddContextoTelemetria(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(ContextoTelemetria)))
            return services;

        services.AddSingleton<ContextoTelemetria>();
        services.AddSingleton<IContextoTelemetria>(sp => sp.GetRequiredService<ContextoTelemetria>());
        return services;
    }
}
