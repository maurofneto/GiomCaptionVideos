using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Microsoft.Extensions.Hosting;
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
            .Enrich.WithThreadId();
    })
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        services.AddEmbeddingService(config);      // SupabaseEdgeFunction — retrieval (RAG)
        services.AddAgentService(config);          // Claude
        services.AddChatChannel(config);           // Telegram
        services.AddConversaHistorico(config);     // InMemory (padrão) | Supabase | MongoDB
        services.AddBotComandos(config);           // ajuda/sobre/limpar — cada canal decide como invocar
        services.AddChatBotProcessor();
    })
    .Build();

await host.StartAsync();

var lifetime  = host.Services.GetRequiredService<IHostApplicationLifetime>();
var processor = host.Services.GetRequiredService<ChatBotProcessor>();
await processor.RunAsync(lifetime.ApplicationStopping);

await host.StopAsync();
