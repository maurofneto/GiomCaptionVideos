using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.Sentry;

public static class ServiceCollectionExtensions
{
    /// <summary>Registra só o tipo concreto — Composition monta o ITelemetriaService (composite com outros sinks).</summary>
    public static IServiceCollection AddSentryTelemetria(this IServiceCollection services)
    {
        services.AddSingleton<SentryTelemetriaService>();
        return services;
    }

    /// <summary>Único provider de tracing hoje — liga ITracingService direto, sem indireção na Composition.</summary>
    public static IServiceCollection AddSentryTracing(this IServiceCollection services)
    {
        services.AddSingleton<ITracingService, SentryTracingService>();
        return services;
    }
}
