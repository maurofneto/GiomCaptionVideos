using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Sentry;
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

        services.AddPersistence(config);           // Supabase — OpenAIEmbeddingService depende de IVideoChunkRepository (só usado na indexação, mas exigido pelo construtor)
        services.AddEmbeddingService(config);      // Embedding:FallbackChain (OpenAI) — retrieval (RAG), embedding da pergunta calculado aqui e filtrado por origem no Supabase
        services.AddIncertezaDetectorService(config); // Heuristica (padrão) — usado pelo agente pra marcar telemetria
        services.AddGuardRails(config);            // GuardRails:Habilitados — segurança/confiabilidade sobre pergunta e resposta
        services.AddAgentService(config);          // Claude
        services.AddChatChannels(config);          // Chat:Canais — Telegram, WhatsApp...
        services.AddConversaHistorico(config);     // InMemory (padrão) | Supabase | MongoDB
        services.AddBotComandos(config);           // ajuda/sobre/limpar — cada canal decide como invocar
        services.AddTelemetriaService(config);     // Supabase + Sentry — auditoria de operações de IA
        services.AddTracingService(config);         // Sentry Performance — quebra de tempo por etapa
        services.AddObservabilidadeExterna(config); // Helicone — Sessions/Users/Properties/Feedback
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

// Os laços que o bot mantém rodando (um ChatBotProcessor por canal de chat, mais o consumidor
// da fila do webhook do WhatsApp) são decididos pela composição a partir da config. Aqui só
// rodamos todos em paralelo — este arquivo não sabe quais canais existem nem quantos são.
var canaisAtivos = host.Services.GetServices<CanalDeChatAtivo>().ToList();
Log.Information("Canais ativos: {Canais}", string.Join(", ", canaisAtivos.Select(c => c.Chave)));

await Task.WhenAll(canaisAtivos.Select(canal => canal.Executar(lifetime.ApplicationStopping)));

await host.StopAsync();
