using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Giom.VideoManager.Infrastructure.Sentry;

public static class ServiceCollectionExtensions
{
    /// <summary>Registrado como ITelemetriaSink (não ITelemetriaService) — CompositeTelemetriaService agrega todo ITelemetriaSink automaticamente via DI.</summary>
    public static IServiceCollection AddSentryTelemetria(this IServiceCollection services)
    {
        // SentryTelemetriaService depende de ISessionContext — registra aqui (idempotente) em
        // vez de depender de outro Add* (ex: AddSummaryService) registrar de carona. Workers só
        // de ingestão (ex: Instituto.Worker pós-#104) não chamam nenhum provider de IA, então
        // essa carona deixou de existir.
        services.AddSessionContext();
        services.AddSingleton<ITelemetriaSink, SentryTelemetriaService>();
        return services;
    }

    /// <summary>
    /// Único provider de tracing hoje — liga ITracingService, e inicializa o TracerProvider do
    /// OpenTelemetry + o SentrySdk na ordem exigida (ver SentryBootstrap — TracerProvider
    /// primeiro, SentrySdk.Init depois). Precisa de IConfiguration aqui (não só no Serilog)
    /// porque essa é a única forma de garantir que a inicialização roda ANTES de qualquer
    /// Activity ser criado (ChannelProcessor só chama IniciarTrace bem depois de
    /// ConfigureServices terminar). Registrado como IHostedService (ver
    /// SentryBootstrapHostedService) — StartAsync roda antes do código principal (garante a
    /// ordem), StopAsync descarta e força o flush; precisa que a aplicação chame
    /// host.StartAsync()/StopAsync() (todos os workers e a ferramenta de teste fazem isso).
    /// </summary>
    public static IServiceCollection AddSentryTracing(this IServiceCollection services, IConfiguration config)
    {
        // SentryTracingService também depende de ISessionContext — mesmo motivo do
        // AddSentryTelemetria acima.
        services.AddSessionContext();
        services.AddSingleton<ITracingService, SentryTracingService>();
        // Sem factory: IConfiguration já é registrado pelo Generic Host — o construtor da
        // hosted service resolve sozinho.
        services.AddHostedService<SentryBootstrapHostedService>();
        return services;
    }
}
