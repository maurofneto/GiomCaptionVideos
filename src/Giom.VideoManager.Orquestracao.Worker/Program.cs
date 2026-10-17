using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
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

        services.AddPersistence(config);   // só pra marcar ProcessedAt no terminal
        services.AddTelemetriaService(config);
        services.AddTracingService(config);

        // Fila de entrada (eventos de conclusão) + 1 publisher keyed por etapa de saída — o
        // PipelineHandler resolve o publisher certo em runtime a partir da topologia.
        services.AddFilaVideoEtapa<EtapaConcluidaMensagem>("etapa-concluida", config, "FilaEtapaConcluida");
        services.AddFilaVideoEtapa<VideoEtapaMensagem>("transcricao", config, "FilaTranscricao");
        services.AddFilaVideoEtapa<VideoEtapaMensagem>("resumo", config, "FilaResumo");
        services.AddFilaVideoEtapa<VideoEtapaMensagem>("categorizacao", config, "FilaCategorizacao");
        services.AddFilaVideoEtapa<VideoEtapaMensagem>("embedding", config, "FilaEmbedding");

        services.AddTopologia(config);     // Config First: etapa_topologia (banco), não appsettings
        services.AddPipelineOrquestrador(config);
    })
    .Build();

await host.StartAsync();

await host.Services.GetRequiredService<EtapaConcluidaQueueConsumer>().ConsumirAsync();

await host.StopAsync();
