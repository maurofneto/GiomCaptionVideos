using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Embedding.Worker;
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
        services.AddEmbeddingService(config); // OpenAI (padrão) — VoyageAI selecionável, sem fallback automático (Feature #101)
        services.AddTelemetriaService(config);
        services.AddTracingService(config);
        services.AddObservabilidadeExterna(config);

        services.AddSingleton<EmbeddingEtapaHandler>();
        services.AddSingleton<IEtapaEmbeddingHandler>(sp => sp.GetRequiredService<EmbeddingEtapaHandler>());

        services.AddFilaVideoEtapa<VideoEtapaMensagem>("embedding", config, "FilaEmbedding");
        services.AddEtapaConsumer<IEtapaEmbeddingHandler>("embedding", "video.embedding", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa<EtapaConcluidaMensagem>("etapa-concluida", config, "FilaEtapaConcluida");
    })
    .Build();

await host.StartAsync();

await host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("embedding").ConsumirAsync();

await host.StopAsync();
