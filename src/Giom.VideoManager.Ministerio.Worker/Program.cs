using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Ministerio.Worker;
using Serilog;

var host = Host.CreateDefaultBuilder(args)
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
            .AddSentryIfConfigured(context.Configuration, environment: "ministerio");
    })
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        services.AddVideoSourceService(config);
        services.AddPersistence(config);
        services.AddTranscriptionService(config);  // YouTubeCaption por padrão
        services.AddSummaryService(config);        // Claude por padrão
        services.AddTelemetriaService(config);     // Supabase — auditoria de operações de IA
        services.AddChannelProcessor(config);

        // Handler específico do Ministério
        services.AddSingleton<IVideoHandler, MinisterioVideoHandler>();
    })
    .Build();

// Canais do Ministério Giom
var channels = new[]
{
    new ChannelConfig("UCyHMyZH5oon8jW7y_yMRstQ", null, "Ministério Giom", Mine: false),
};

var processor = host.Services.GetRequiredService<Giom.VideoManager.Application.Pipeline.ChannelProcessor>();
await processor.RunAsync(channels);
