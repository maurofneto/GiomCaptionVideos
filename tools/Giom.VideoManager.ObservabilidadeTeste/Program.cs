using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Instituto.Worker;
using Giom.VideoManager.ObservabilidadeTeste;
using Serilog;

// Mesma composição do Giom.VideoManager.Instituto.Worker (Program.cs) — só troca
// AddVideoSourceService (YouTube real) pelo FakeVideoSourceService, e roda uma vez só.
// Objetivo: exercitar o pipeline real (resumo/categorização/embedding, Sentry, Helicone,
// CorrelationId) sem depender de vídeos reais do YouTube nem gastar cota de API.
var host = Host.CreateDefaultBuilder(args)
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
            .ConfigureSentry(context.Configuration);
    })
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        services.AddSingleton<IVideoSourceService, FakeVideoSourceService>();
        services.AddPersistence(config);           // Supabase dev — grava os vídeos fake de verdade, pra inspecionar depois
        services.AddSingleton<ICaptionService, FakeCaptionService>(); // nunca chamado (Transcricao já vem preenchida no vídeo fake) — evita precisar de credencial OAuth do Google só pra satisfazer o DI
        services.AddSummaryService(config);        // Claude real — via Helicone
        services.AddCategorizacaoService(config);  // Claude real — via Helicone
        services.AddEmbeddingService(config);      // OpenAI real
        services.AddTelemetriaService(config);     // Supabase + Sentry + alerta de gasto
        services.AddTracingService();              // Sentry Performance
        services.AddObservabilidadeExterna(config); // Helicone Sessions/Properties
        services.AddChannelProcessor(config);

        services.AddSingleton<IVideoHandler, InstitutoVideoHandler>();
    })
    .Build();

var canaisFake = new[]
{
    new ChannelConfig("fake-instituto-observabilidade", null, "Instituto Giom (Teste de Observabilidade)", Mine: true),
};

var processor = host.Services.GetRequiredService<Giom.VideoManager.Application.Pipeline.ChannelProcessor>();
await processor.RunAsync(canaisFake);

Log.Information("Execução de teste concluída — confira Sentry (Performance/Logs) e Helicone (Sessions) pra ver o resultado.");
await Log.CloseAndFlushAsync();
