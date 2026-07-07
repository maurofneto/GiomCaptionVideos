using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Sentry.OpenTelemetry;

namespace Giom.VideoManager.Infrastructure.Sentry;

public static class ServiceCollectionExtensions
{
    /// <summary>Registrado como ITelemetriaSink (não ITelemetriaService) — CompositeTelemetriaService agrega todo ITelemetriaSink automaticamente via DI.</summary>
    public static IServiceCollection AddSentryTelemetria(this IServiceCollection services)
    {
        services.AddSingleton<ITelemetriaSink, SentryTelemetriaService>();
        return services;
    }

    /// <summary>
    /// Único provider de tracing hoje — liga ITracingService direto, sem indireção na Composition.
    /// Registra o SDK do OpenTelemetry escutando GiomActivitySource e encaminhando pro Sentry
    /// (Sentry.OpenTelemetry.SentrySpanProcessor traduz Activity em Transaction/Span) — sem isso,
    /// ActivitySource.StartActivity nunca teria um listener e sempre devolveria null. A outra
    /// metade da ligação (SentryOptions.UseOpenTelemetry) fica em
    /// LoggerConfigurationSentryExtensions.ConfigureSentry, onde o Sentry já é inicializado
    /// (via Serilog). Precisa que o host seja efetivamente iniciado (host.StartAsync/RunAsync) —
    /// o TracerProvider só sobe através do IHostedService que este pacote registra.
    /// </summary>
    public static IServiceCollection AddSentryTracing(this IServiceCollection services)
    {
        services.AddSingleton<ITracingService, SentryTracingService>();
        services.AddOpenTelemetry().WithTracing(builder => builder
            .AddSource(GiomActivitySource.Name)
            .AddSentry());
        return services;
    }
}
