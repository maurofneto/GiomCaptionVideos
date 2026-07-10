using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Categorizacao.Worker;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
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
        services.AddCategorizacaoService(config); // Fallback Claude→Grok→OpenAI (Feature #101)
        services.AddTelemetriaService(config);
        services.AddTracingService(config);
        services.AddObservabilidadeExterna(config);

        services.AddSingleton<CategorizacaoEtapaHandler>();
        services.AddSingleton<IEtapaCategorizacaoHandler>(sp => sp.GetRequiredService<CategorizacaoEtapaHandler>());

        services.AddFilaVideoEtapa<VideoEtapaMensagem>("categorizacao", config, "FilaCategorizacao");
        services.AddEtapaConsumer<IEtapaCategorizacaoHandler>("categorizacao", "video.categorizacao", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa<EtapaConcluidaMensagem>("etapa-concluida", config, "FilaEtapaConcluida");
    })
    .Build();

await host.StartAsync();

await host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("categorizacao").ConsumirAsync();

await host.StopAsync();
