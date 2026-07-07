using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Sentry;
using Serilog;

var host = Host.CreateDefaultBuilder(args)
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
            .ConfigureSentry(context.Configuration);
    })
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        services.AddEmbeddingService(config);      // SupabaseEdgeFunction — retrieval (RAG)
        services.AddIncertezaDetectorService(config); // Heuristica (padrão) — usado pelo agente pra marcar telemetria
        services.AddAgentService(config);          // Claude
        services.AddChatChannel(config);           // Telegram
        services.AddConversaHistorico(config);     // InMemory (padrão) | Supabase | MongoDB
        services.AddBotComandos(config);           // ajuda/sobre/limpar — cada canal decide como invocar
        services.AddTelemetriaService(config);     // Supabase + Sentry — auditoria de operações de IA
        services.AddTracingService();              // Sentry Performance — quebra de tempo por etapa
        services.AddChatBotProcessor(config);
    })
    .Build();

// Diagnóstico temporário — Sentry Performance parou de reportar (nem erro nem transação) desde
// o deploy de 2026-07-06 18:25, e preciso confirmar se o Dsn está resolvendo em runtime, já que
// não tenho ECS Exec habilitado pra inspecionar o container ao vivo. Remover depois de confirmar.
var dsnConfigurado = host.Services.GetRequiredService<IConfiguration>()["Sentry:Dsn"];
Log.Information("Diagnóstico Sentry — DsnLength={DsnLength} SentrySdk.IsEnabled={IsEnabled}",
    dsnConfigurado?.Length ?? 0, SentrySdk.IsEnabled);

await host.StartAsync();

var lifetime  = host.Services.GetRequiredService<IHostApplicationLifetime>();
var processor = host.Services.GetRequiredService<ChatBotProcessor>();
await processor.RunAsync(lifetime.ApplicationStopping);

await host.StopAsync();
