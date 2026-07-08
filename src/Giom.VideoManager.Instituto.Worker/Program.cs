using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Instituto.Worker;
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
        services.AddPersistence(config);           // Supabase
        services.AddTranscriptionService(config);  // Whisper
        services.AddSummaryService(config);        // Claude
        services.AddCategorizacaoService(config);  // Claude (Haiku)
        services.AddEmbeddingService(config);      // Supabase pgvector
        services.AddTelemetriaService(config);     // Supabase + Sentry — auditoria de operações de IA
        services.AddTracingService(config);         // Sentry Performance — quebra de tempo por página/vídeo
        services.AddObservabilidadeExterna(config); // Helicone — Sessions/Properties (resumo/categorização)
        services.AddFilaVideo(config);              // Fila:Provider — Sqs (padrão)/RabbitMq — ingestão desacoplada (US #39/#40)
        services.AddChannelProcessor(config);

        // Processador real do Instituto — só usado por FilaVideoHandler pra IsRelevant, quem
        // processa de verdade agora é InstitutoPipelineHandler (Feature #33)
        services.AddSingleton<IVideoProcessador, InstitutoVideoHandler>();

        // Filas de etapa (Feature #33 — US #41/#42/#43): Resumo e Embedding dependem só de
        // Transcrição (fan-out), Categorização depende de Resumo.
        services.AddFilaVideoEtapa("resumo", config, "FilaResumo");
        services.AddFilaVideoEtapa("categorizacao", config, "FilaCategorizacao");
        services.AddFilaVideoEtapa("embedding", config, "FilaEmbedding");

        // Um InstitutoPipelineHandler só, compartilhado pelas 4 interfaces de etapa — ele decide
        // o que publicar em seguida, sabendo o ProcessingOptions do canal.
        services.AddSingleton<InstitutoPipelineHandler>();
        services.AddSingleton<IEtapaTranscricaoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
        services.AddSingleton<IEtapaResumoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
        services.AddSingleton<IEtapaCategorizacaoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
        services.AddSingleton<IEtapaEmbeddingHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
    })
    .Build();

// Canais do Instituto Giom
var channels = new[]
{
    new ChannelConfig("UCyyXFx2xHdzfavx6H6Q8zJw", null, "Instituto Giom", Mine: true),
};

// StartAsync/StopAsync (não só resolver os serviços direto) — sem isso o IHostedService que sobe
// o TracerProvider do OpenTelemetry (registrado em AddSentryTracing) nunca roda, e todo Activity
// vira no-op (ActivitySource.StartActivity sempre devolve null).
await host.StartAsync();

var processor = host.Services.GetRequiredService<ChannelProcessor>();
await processor.RunAsync(channels);

// Feature #33 — 4 consumers (Transcrição/Resumo/Categorização/Embedding) rodando concorrentes,
// não em sequência: o objetivo é Resumo/Embedding começarem assim que o PRIMEIRO vídeo estiver
// transcrito, não esperar o lote inteiro da Transcrição terminar (isso anularia o ganho da US
// #41/#43). VideoQueueConsumer é a etapa de Transcrição (drena a fila de ingestão da US #39/#40).
var transcricaoConsumer = host.Services.GetRequiredService<VideoQueueConsumer>();

var resumoConsumer = new EtapaQueueConsumer(
    host.Services.GetRequiredKeyedService<IFilaConsumer<VideoEtapaMensagem>>("resumo"),
    (video, ct) => host.Services.GetRequiredService<IEtapaResumoHandler>().ProcessarAsync(video, ct),
    "video.resumo",
    host.Services.GetRequiredService<ITracingService>(),
    host.Services.GetRequiredService<ISessionContext>(),
    host.Services.GetRequiredService<ILogger<EtapaQueueConsumer>>());

var categorizacaoConsumer = new EtapaQueueConsumer(
    host.Services.GetRequiredKeyedService<IFilaConsumer<VideoEtapaMensagem>>("categorizacao"),
    (video, ct) => host.Services.GetRequiredService<IEtapaCategorizacaoHandler>().ProcessarAsync(video, ct),
    "video.categorizacao",
    host.Services.GetRequiredService<ITracingService>(),
    host.Services.GetRequiredService<ISessionContext>(),
    host.Services.GetRequiredService<ILogger<EtapaQueueConsumer>>());

var embeddingConsumer = new EtapaQueueConsumer(
    host.Services.GetRequiredKeyedService<IFilaConsumer<VideoEtapaMensagem>>("embedding"),
    (video, ct) => host.Services.GetRequiredService<IEtapaEmbeddingHandler>().ProcessarAsync(video, ct),
    "video.embedding",
    host.Services.GetRequiredService<ITracingService>(),
    host.Services.GetRequiredService<ISessionContext>(),
    host.Services.GetRequiredService<ILogger<EtapaQueueConsumer>>());

await Task.WhenAll(
    transcricaoConsumer.ConsumirAsync(),
    resumoConsumer.ConsumirAsync(),
    categorizacaoConsumer.ConsumirAsync(),
    embeddingConsumer.ConsumirAsync());

await host.StopAsync();
