using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.Sentry;

public static class ServiceCollectionExtensions
{
    /// <summary>Registrado como ITelemetriaSink (não ITelemetriaService) — CompositeTelemetriaService agrega todo ITelemetriaSink automaticamente via DI.</summary>
    public static IServiceCollection AddSentryTelemetria(this IServiceCollection services)
    {
        services.AddSingleton<ITelemetriaSink, SentryTelemetriaService>();
        return services;
    }

    /// <summary>Único provider de tracing hoje — liga ITracingService direto, sem indireção na Composition.</summary>
    public static IServiceCollection AddSentryTracing(this IServiceCollection services)
    {
        services.AddSingleton<ITracingService, SentryTracingService>();
        return services;
    }
}
