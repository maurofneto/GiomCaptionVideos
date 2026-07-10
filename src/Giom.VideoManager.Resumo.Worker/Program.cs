using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Resumo.Worker;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true);
        config.AddEnvironmentVariables();
        config.AddSecretsProvider(config.Build());
    })
    .UseSerilog((context, _, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.With<ShortSourceContextEnricher>()
            .Enrich.With<SessionContextEnricher>()
            .AddSentrySink(context.Configuration);
    })
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        services.AddPersistence(config);
        services.AddSummaryService(config);        // Fallback Claude→Grok→OpenAI (Feature #101), credencial unificada entre canais (Feature #104)
        services.AddTelemetriaService(config);
        services.AddTracingService(config);
        services.AddObservabilidadeExterna(config);

        services.AddSingleton<ResumoEtapaHandler>();
        services.AddSingleton<IEtapaResumoHandler>(sp => sp.GetRequiredService<ResumoEtapaHandler>());

        services.AddFilaVideoEtapa<VideoEtapaMensagem>("resumo", config, "FilaResumo");
        services.AddEtapaConsumer<IEtapaResumoHandler>("resumo", "video.resumo", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa<EtapaConcluidaMensagem>("etapa-concluida", config, "FilaEtapaConcluida");
    })
    .Build();

await host.StartAsync();

await host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("resumo").ConsumirAsync();

await host.StopAsync();
