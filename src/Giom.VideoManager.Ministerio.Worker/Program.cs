using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Ministerio.Worker;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true);
        config.AddEnvironmentVariables();
        // Pluggable secrets — troque "Configuration" por "AwsParameterStore" ou "AzureAppConfiguration" em produção
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

        services.AddVideoSourceService(config);
        services.AddPersistence(config);
        services.AddTranscriptionService(config);  // YouTubeCaption por padrão
        services.AddSummaryService(config);        // Claude por padrão
        services.AddTelemetriaService(config);     // Supabase + Sentry — auditoria de operações de IA
        services.AddTracingService(config);         // Sentry Performance — quebra de tempo por página/vídeo
        services.AddObservabilidadeExterna(config); // Helicone — Sessions/Properties (resumo)
        services.AddFilaVideo(config);              // Fila:Provider — Sqs (padrão)/RabbitMq — ingestão desacoplada (US #39/#40)
        services.AddChannelProcessor(config);

        // Processador real do Ministério — só usado por FilaVideoHandler pra IsRelevant, quem
        // processa de verdade agora é MinisterioPipelineHandler (Feature #33)
        services.AddSingleton<IVideoProcessador, MinisterioVideoHandler>();

        // Fila de etapa (Feature #33 — US #41/#42/#43): Ministério só tem Transcrição+Resumo,
        // não implementa Categorização/Embedding (ISP).
        services.AddFilaVideoEtapa("resumo", config, "FilaResumo");

        services.AddSingleton<MinisterioPipelineHandler>();
        services.AddSingleton<IEtapaTranscricaoHandler>(sp => sp.GetRequiredService<MinisterioPipelineHandler>());
        services.AddSingleton<IEtapaResumoHandler>(sp => sp.GetRequiredService<MinisterioPipelineHandler>());
    })
    .Build();

// Canais do Ministério Giom
var channels = new[]
{
    new ChannelConfig("UCyHMyZH5oon8jW7y_yMRstQ", null, "Ministério Giom", Mine: false),
};

// StartAsync/StopAsync (não só resolver os serviços direto) — sem isso o IHostedService que sobe
// o TracerProvider do OpenTelemetry (registrado em AddSentryTracing) nunca roda, e todo Activity
// vira no-op (ActivitySource.StartActivity sempre devolve null).
await host.StartAsync();

var processor = host.Services.GetRequiredService<ChannelProcessor>();
await processor.RunAsync(channels);

// Feature #33 — 2 consumers (Transcrição/Resumo) rodando concorrentes, não em sequência: o
// objetivo é o Resumo começar assim que o PRIMEIRO vídeo estiver transcrito, não esperar o lote
// inteiro terminar. VideoQueueConsumer é a etapa de Transcrição (drena a fila de ingestão da US
// #39/#40). Ministério não tem Categorização/Embedding.
var transcricaoConsumer = host.Services.GetRequiredService<VideoQueueConsumer>();

var resumoConsumer = new EtapaQueueConsumer(
    host.Services.GetRequiredKeyedService<IFilaConsumer<VideoEtapaMensagem>>("resumo"),
    (video, ct) => host.Services.GetRequiredService<IEtapaResumoHandler>().ProcessarAsync(video, ct),
    "video.resumo",
    host.Services.GetRequiredService<ITracingService>(),
    host.Services.GetRequiredService<ISessionContext>(),
    host.Services.GetRequiredService<ILogger<EtapaQueueConsumer>>());

await Task.WhenAll(
    transcricaoConsumer.ConsumirAsync(),
    resumoConsumer.ConsumirAsync());

await host.StopAsync();
