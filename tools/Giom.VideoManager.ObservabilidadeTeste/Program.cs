using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Infrastructure.Telemetria;
using Giom.VideoManager.Instituto.Worker;
using Giom.VideoManager.ObservabilidadeTeste;
using Microsoft.Extensions.Logging;
using Serilog;

// Mesma composição do Giom.VideoManager.Instituto.Worker (Program.cs) — só troca
// AddVideoSourceService (YouTube real) pelo SupabaseVideoSource, e roda uma vez só.
// Objetivo: exercitar o pipeline real (resumo/categorização/embedding, Sentry, Helicone,
// CorrelationId) sem depender do YouTube — usando vídeos/legendas reais já existentes no
// Supabase (dev), mas devolvidos com Id novo pra sempre reprocessar do zero.
using var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development"}.json", optional: true);
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

        services.AddPersistence(config);           // Supabase dev — SupabaseVideoSource/SupabaseCaptionService leem daqui, e o vídeo de teste também é gravado aqui de verdade
        services.AddSingleton<IVideoSourceService, SupabaseVideoSource>();
        // Decorado com TelemetriaCaptionService — igual AddYouTubeCaptionService faz pros
        // providers reais — senão a etapa "transcricao" nunca abre span/métrica nesta ferramenta.
        services.AddSingleton<SupabaseCaptionService>();
        services.AddSingleton<ICaptionService>(sp => new TelemetriaCaptionService(
            sp.GetRequiredService<SupabaseCaptionService>(),
            sp.GetRequiredService<ITelemetriaService>(),
            sp.GetRequiredService<ITracingService>(),
            sp.GetRequiredService<ILogger<TelemetriaCaptionService>>()));
        services.AddSummaryService(config);        // Claude real — via Helicone
        services.AddCategorizacaoService(config);  // Claude real — via Helicone
        services.AddEmbeddingService(config);      // OpenAI real
        services.AddTelemetriaService(config);     // Supabase + Sentry + alerta de gasto
        services.AddTracingService(config);         // Sentry Performance
        services.AddObservabilidadeExterna(config); // Helicone Sessions/Properties
        services.AddFilaVideo(config);              // Fila:Provider — Sqs (padrão)/RabbitMq — ingestão desacoplada (US #39/#40)
        services.AddChannelProcessor(config);

        services.AddSingleton<IVideoProcessador, InstitutoVideoHandler>();
    })
    .Build();

var canalDeTeste = new[]
{
    new ChannelConfig("supabase-instituto-teste", null, "Instituto Giom (Teste de Observabilidade)", Mine: true),
};

// StartAsync/StopAsync (não só resolver os serviços direto) — sem isso o IHostedService que
// inicializa o Sentry/OpenTelemetry (registrado em AddSentryTracing) nunca roda, e nada é
// enviado pro Sentry (nem inicializado, nem descartado/flushado no fim).
await host.StartAsync();

var processor = host.Services.GetRequiredService<Giom.VideoManager.Application.Pipeline.ChannelProcessor>();
await processor.RunAsync(canalDeTeste);

// Drena a fila (US #39/#40) — mesma execução processa o que a descoberta acabou de publicar.
var consumidor = host.Services.GetRequiredService<Giom.VideoManager.Application.Pipeline.VideoQueueConsumer>();
await consumidor.ConsumirAsync();

await host.StopAsync();

Log.Information("Execução de teste concluída — confira Sentry (Performance/Logs) e Helicone (Sessions) pra ver o resultado.");
await Log.CloseAndFlushAsync();
