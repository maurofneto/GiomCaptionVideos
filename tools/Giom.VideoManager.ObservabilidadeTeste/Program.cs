using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Infrastructure.Telemetria;
using Giom.VideoManager.Instituto.Worker;
using Giom.VideoManager.ObservabilidadeTeste;
using Microsoft.Extensions.Logging;
using Serilog;

// Mesma composição do Giom.VideoManager.Instituto.Worker (Program.cs) — só troca
// AddVideoSourceService (YouTube real) pelo SupabaseVideoSource, e roda uma vez só.
// Objetivo: exercitar o pipeline real (resumo/categorização/embedding, Sentry, Helicone,
// CorrelationId) sem depender do YouTube nem de nenhuma chamada de IA — Resumo/Categorização
// reaproveitam conteúdo real já gerado no Supabase (mesmo padrão de SupabaseCaptionService),
// Embedding usa vetores fake (dimensão certa, sem semântica real) e injeta falha determinística
// por vídeo pra exercitar o retry dinâmico da Feature #34 (ver FakeEmbeddingService).
using var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development"}.json", optional: true);
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

        services.AddPersistence(config);           // Supabase dev — SupabaseVideoSource/SupabaseCaptionService leem daqui, e o vídeo de teste também é gravado aqui de verdade
        services.AddSingleton<IVideoSourceService, SupabaseVideoSource>();
        // Decorado com TelemetriaCaptionService — igual AddYouTubeCaptionService faz pros
        // providers reais — senão a etapa "transcricao" nunca abre span/métrica nesta ferramenta.
        services.AddSingleton<SupabaseCaptionService>();
        services.AddSingleton<ICaptionService>(sp => new TelemetriaCaptionService(
            sp.GetRequiredService<SupabaseCaptionService>(),
            sp.GetRequiredService<ITelemetriaService>(),
            sp.GetRequiredService<ITracingService>(),
            sp.GetRequiredService<ILogger<TelemetriaCaptionService>>()));
        // Sem IA nem YouTube: Resumo/Categorização reaproveitam conteúdo real do Supabase
        // (idêntico ao SupabaseCaptionService pra transcrição); Embedding é fake com falha
        // determinística por vídeo (ver FakeEmbeddingService) — nada disso chama Claude/OpenAI.
        services.AddSingleton<IResumoService, SupabaseResumoService>();
        services.AddSingleton<ICategorizacaoService, SupabaseCategorizacaoService>();
        services.AddSingleton<IEmbeddingService, FakeEmbeddingService>();
        // Antes vinha de dentro de AddSummaryService/AddCategorizacaoService (Claude real) —
        // removidos daqui, então precisa ser chamado direto: ISessionContext é usado por
        // SentryTracingService, InstitutoPipelineHandler etc.
        services.AddSessionContext();
        services.AddTelemetriaService(config);     // Supabase + Sentry + alerta de gasto
        services.AddTracingService(config);         // Sentry Performance
        services.AddObservabilidadeExterna(config); // Helicone Sessions/Properties
        services.AddChannelProcessor(config);

        services.AddSingleton<InstitutoPipelineHandler>();
        services.AddSingleton<IVideoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
        services.AddSingleton<IEtapaTranscricaoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
        services.AddSingleton<IEtapaResumoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
        services.AddSingleton<IEtapaCategorizacaoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
        services.AddSingleton<IEtapaEmbeddingHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());

        // Filas + consumers de etapa (Feature #33) — mesma composição do Instituto.Worker/Program.cs
        services.AddFilaVideoEtapa("transcricao", config, "FilaTranscricao");
        services.AddEtapaConsumer<IEtapaTranscricaoHandler>("transcricao", "video.transcricao", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa("resumo", config, "FilaResumo");
        services.AddEtapaConsumer<IEtapaResumoHandler>("resumo", "video.resumo", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa("categorizacao", config, "FilaCategorizacao");
        services.AddEtapaConsumer<IEtapaCategorizacaoHandler>("categorizacao", "video.categorizacao", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa("embedding", config, "FilaEmbedding");
        services.AddEtapaConsumer<IEtapaEmbeddingHandler>("embedding", "video.embedding", (h, v, ct) => h.ProcessarAsync(v, ct));
    })
    .Build();

var canalDeTeste = new[]
{
    new ChannelConfig("supabase-instituto-teste", null, "Instituto Giom (Teste de Observabilidade)", Mine: true),
};

// StartAsync/StopAsync (não só resolver os serviços direto) — sem isso o IHostedService que
// inicializa o Sentry/OpenTelemetry (registrado em AddSentryTracing) nunca roda, e nada é
// enviado pro Sentry (nem inicializado, nem descartado/flushado no fim).
await host.StartAsync();

var processor = host.Services.GetRequiredService<Giom.VideoManager.Application.Pipeline.ChannelProcessor>();
await processor.RunAsync(canalDeTeste);

// Feature #33 — 4 consumers (Transcrição/Resumo/Categorização/Embedding) concorrentes, mesma
// composição do Instituto.Worker/Program.cs — já registrados via AddEtapaConsumer.
await Task.WhenAll(
    host.Services.GetRequiredKeyedService<Giom.VideoManager.Application.Pipeline.EtapaQueueConsumer>("transcricao").ConsumirAsync(),
    host.Services.GetRequiredKeyedService<Giom.VideoManager.Application.Pipeline.EtapaQueueConsumer>("resumo").ConsumirAsync(),
    host.Services.GetRequiredKeyedService<Giom.VideoManager.Application.Pipeline.EtapaQueueConsumer>("categorizacao").ConsumirAsync(),
    host.Services.GetRequiredKeyedService<Giom.VideoManager.Application.Pipeline.EtapaQueueConsumer>("embedding").ConsumirAsync());

await host.StopAsync();

Log.Information("Execução de teste concluída — confira Sentry (Performance/Logs) e Helicone (Sessions) pra ver o resultado.");
await Log.CloseAndFlushAsync();
