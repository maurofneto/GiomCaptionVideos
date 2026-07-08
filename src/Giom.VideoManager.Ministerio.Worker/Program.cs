using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Ministerio.Worker;
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
        services.AddPersistence(config);
        services.AddTranscriptionService(config);  // YouTubeCaption por padrão
        services.AddSummaryService(config);        // Claude por padrão
        services.AddTelemetriaService(config);     // Supabase + Sentry — auditoria de operações de IA
        services.AddTracingService(config);         // Sentry Performance — quebra de tempo por página/vídeo
        services.AddObservabilidadeExterna(config); // Helicone — Sessions/Properties (resumo)
        services.AddChannelProcessor(config);

        // Dono da própria topologia de descoberta (IVideoHandler), sem FilaVideoHandler genérico.
        services.AddSingleton<MinisterioPipelineHandler>();
        services.AddSingleton<IVideoHandler>(sp => sp.GetRequiredService<MinisterioPipelineHandler>());
        services.AddSingleton<IEtapaTranscricaoHandler>(sp => sp.GetRequiredService<MinisterioPipelineHandler>());
        services.AddSingleton<IEtapaResumoHandler>(sp => sp.GetRequiredService<MinisterioPipelineHandler>());

        // Filas + consumers de etapa (Feature #33 — US #41/#42/#43): Transcrição não é caso
        // especial, usa o mesmo mecanismo das demais. Ministério só tem Transcrição+Resumo, não
        // implementa Categorização/Embedding (ISP).
        services.AddFilaVideoEtapa("transcricao", config, "FilaTranscricao");
        services.AddEtapaConsumer<IEtapaTranscricaoHandler>("transcricao", "video.transcricao", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa("resumo", config, "FilaResumo");
        services.AddEtapaConsumer<IEtapaResumoHandler>("resumo", "video.resumo", (h, v, ct) => h.ProcessarAsync(v, ct));
    })
    .Build();

// Canais do Ministério Giom
var channels = new[]
{
    new ChannelConfig("UCyHMyZH5oon8jW7y_yMRstQ", null, "Ministério Giom", Mine: false),
};

// StartAsync/StopAsync (não só resolver os serviços direto) — sem isso o IHostedService que sobe
// o TracerProvider do OpenTelemetry (registrado em AddSentryTracing) nunca roda, e todo Activity
// vira no-op (ActivitySource.StartActivity sempre devolve null).
await host.StartAsync();

var processor = host.Services.GetRequiredService<ChannelProcessor>();
await processor.RunAsync(channels);

// Feature #33 — 2 consumers (Transcrição/Resumo) rodando concorrentes, não em sequência: o
// objetivo é o Resumo começar assim que o PRIMEIRO vídeo estiver transcrito, não esperar o lote
// inteiro terminar. Ministério não tem Categorização/Embedding.
await Task.WhenAll(
    host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("transcricao").ConsumirAsync(),
    host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("resumo").ConsumirAsync());

await host.StopAsync();
