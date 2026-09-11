using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Transcricao.Worker;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true);
        config.AddEnvironmentVariables();
        // Pluggable secrets — Paths carrega pipeline/ + canais/ (US #146)
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

        services.AddPersistence(config);          // Supabase — compartilhado entre canais, não é por-canal
        services.AddTranscriptionService(config);  // Fallback YouTubeCaption→YtDlpCaption→Whisper (Feature #101)
        // US #146 — OAuth Google + cookies yt-dlp keyed por ChannelId (mesmo padrão da Ingestão #105).
        // Substitui o cookie único escrito em disco no boot (Bug #144 / pipeline/YtDlp/CookiesContent).
        services.AddTranscricaoMultiCanal(config);
        services.AddTelemetriaService(config);
        services.AddTracingService(config);
        services.AddObservabilidadeExterna(config);

        // Handler genérico (Feature #104) — sem saber de canal, resolve tudo por config única.
        services.AddSingleton<TranscricaoEtapaHandler>();
        services.AddSingleton<IEtapaTranscricaoHandler>(sp => sp.GetRequiredService<TranscricaoEtapaHandler>());

        // Fila única da etapa (Instituto e Ministério publicam nela — Video.ChannelId já vem na
        // mensagem) + fila de eventos de conclusão, consumida pelo orquestrador (Orquestracao.Worker).
        services.AddFilaVideoEtapa<VideoEtapaMensagem>("transcricao", config, "FilaTranscricao");
        services.AddEtapaConsumer<IEtapaTranscricaoHandler>("transcricao", "video.transcricao", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa<EtapaConcluidaMensagem>("etapa-concluida", config, "FilaEtapaConcluida");
    })
    .Build();

// StartAsync/StopAsync (não só resolver os serviços direto) — sem isso o IHostedService que sobe
// o TracerProvider do OpenTelemetry (registrado em AddSentryTracing) nunca roda, e todo Activity
// vira no-op (ActivitySource.StartActivity sempre devolve null).
await host.StartAsync();

// 1 fila só (drena até vazio e sai — mesmo modelo run-to-completion de sempre). Não é mais
// Task.WhenAll de várias etapas: cada worker de etapa agora é 1 consumer só.
await host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("transcricao").ConsumirAsync();

await host.StopAsync();
