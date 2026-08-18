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

        // WhatsApp (US #24) — o fluxo está completo desde a #141: webhook -> fila -> processor
        // -> canal, com um ChatBotProcessor próprio escutando junto do Telegram. A flag continua
        // nascendo false porque ligar exige credencial real da Meta (PhoneNumberId/AccessToken
        // no SSM) e um número registrado no WABA; sem isso o canal subiria só pra falhar em
        // toda resposta.
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

// Um ChatBotProcessor por canal ligado (US #24): cada um escuta o seu e mantém o próprio
// controle de perguntas em andamento. Quais canais estão ligados é decisão da composição — o
// entrypoint só resolve o que foi registrado, então acrescentar um canal um dia não mexe aqui.
var tarefas = host.Services.GetServices<CanalDeChatAtivo>()
    .Select(canal => host.Services.GetRequiredKeyedService<ChatBotProcessor>(canal.Chave))
    .Select(processor => processor.RunAsync(lifetime.ApplicationStopping))
    .ToList();

// O WhatsApp acrescenta um segundo laço: além do processor escutando o canal, o consumidor
// drenando a fila que a Lambda do webhook alimenta. GetService (e não GetRequiredService)
// porque com WhatsApp:Habilitado=false ele nem chega a ser registrado.
if (host.Services.GetService<WhatsAppWebhookConsumidor>() is { } consumidorWhatsApp)
    tarefas.Add(consumidorWhatsApp.ExecutarAsync(lifetime.ApplicationStopping));

await Task.WhenAll(tarefas);

await host.StopAsync();
