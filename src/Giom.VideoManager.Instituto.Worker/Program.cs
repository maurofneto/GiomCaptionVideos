using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Instituto.Worker;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

using var host = Host.CreateDefaultBuilder(args)
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
            .Enrich.With<SessionContextEnricher>()
            .AddSentrySink(context.Configuration);
    })
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        services.AddVideoSourceService(config);
        services.AddPersistence(config);           // Supabase
        services.AddTelemetriaService(config);     // Supabase + Sentry — auditoria de operações de IA
        services.AddTracingService(config);         // Sentry Performance — quebra de tempo por página/vídeo
        services.AddChannelProcessor(config);

        // Feature #104 — Instituto.Worker só cuida de ingestão desde a #104: filtra relevância e
        // publica na fila única de Transcrição (compartilhada com o Ministério, Video.ChannelId
        // já identifica de quem é o vídeo). O processamento de cada etapa (Transcrição/Resumo/
        // Categorização/Embedding) saiu daqui — vive nos workers de etapa próprios, orquestrado
        // pelo PipelineHandler central. InstitutoPipelineHandler não implementa mais IEtapaXHandler.
        services.AddSingleton<InstitutoPipelineHandler>();
        services.AddSingleton<IVideoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());

        services.AddFilaVideoEtapa<VideoEtapaMensagem>("transcricao", config, "FilaTranscricao");
    })
    .Build();

// Canais do Instituto Giom
var channels = new[]
{
    new ChannelConfig("UCyyXFx2xHdzfavx6H6Q8zJw", null, "Instituto Giom", Mine: true),
};

// StartAsync/StopAsync (não só resolver os serviços direto) — sem isso o IHostedService que sobe
// o TracerProvider do OpenTelemetry (registrado em AddSentryTracing) nunca roda, e todo Activity
// vira no-op (ActivitySource.StartActivity sempre devolve null).
await host.StartAsync();

var processor = host.Services.GetRequiredService<ChannelProcessor>();
await processor.RunAsync(channels);

await host.StopAsync();
