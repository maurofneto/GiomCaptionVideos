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
        services.AddPersistence(config);           // Supabase
        services.AddTranscriptionService(config);  // Whisper
        services.AddSummaryService(config);        // Claude
        services.AddCategorizacaoService(config);  // Claude (Haiku)
        services.AddEmbeddingService(config);      // Supabase pgvector
        services.AddChannelProcessor(config);

        // Handler específico do Instituto
        services.AddSingleton<IVideoHandler, InstitutoVideoHandler>();
    })
    .Build();

// Canais do Instituto Giom
var channels = new[]
{
    new ChannelConfig("UCyyXFx2xHdzfavx6H6Q8zJw", null, "Instituto Giom", Mine: true),
};

var processor = host.Services.GetRequiredService<Application.Pipeline.ChannelProcessor>();
await processor.RunAsync(channels);
