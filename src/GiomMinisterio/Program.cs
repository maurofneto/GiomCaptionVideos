using Application.Handlers;
using Domain.ValueObjects;
using Infrastructure.Extensions;
using Serilog;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true);
        config.AddEnvironmentVariables();
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

        services.AddYouTubeServices(config);
        services.AddPersistence(config);
        services.AddTranscriptionService(config);  // YouTubeCaption por padrão
        services.AddSummaryService(config);        // Claude por padrão
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

var processor = host.Services.GetRequiredService<Application.Pipeline.ChannelProcessor>();
await processor.RunAsync(channels);
