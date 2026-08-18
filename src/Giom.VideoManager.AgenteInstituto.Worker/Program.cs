using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Infrastructure.WhatsApp;
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
        services.AddChatChannel(config);           // Telegram

        // WhatsApp (US #24) fica DESLIGADO por padrão. O caminho de entrada já funciona ponta a
        // ponta (Lambda -> fila -> processor), mas o ChatBotProcessor ainda escuta um único
        // IChatChannel, que é o Telegram — então pergunta livre vinda do WhatsApp pararia no
        // inbox sem ninguém ler. Só a Task #141 (rodar um processor por canal) fecha isso; até
        // lá, ligar a flag entregaria um bot que responde /ajuda e engole pergunta de aluno.
        if (config.GetValue<bool>("WhatsApp:Habilitado"))
            services.AddWhatsAppWebhook(config);
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
var processor = host.Services.GetRequiredService<ChatBotProcessor>();

// Task.WhenAll (e não um await só) porque o WhatsApp acrescenta um segundo laço infinito ao
// processo: o ChatBotProcessor escutando o canal e o consumidor drenando a fila do webhook.
// É o mesmo movimento de que a Task #141 vai precisar pra manter dois canais de pé.
var tarefas = new List<Task> { processor.RunAsync(lifetime.ApplicationStopping) };

// GetService (e não GetRequiredService): quando WhatsApp:Habilitado é false o consumidor nem
// chega a ser registrado, e aí o bot roda só com o Telegram, como sempre rodou.
if (host.Services.GetService<WhatsAppWebhookConsumidor>() is { } consumidorWhatsApp)
    tarefas.Add(consumidorWhatsApp.ExecutarAsync(lifetime.ApplicationStopping));

await Task.WhenAll(tarefas);

await host.StopAsync();
