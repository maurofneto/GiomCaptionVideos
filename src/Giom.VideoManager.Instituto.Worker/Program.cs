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
        services.AddTranscriptionService(config);  // Whisper
        services.AddSummaryService(config);        // Claude
        services.AddCategorizacaoService(config);  // Claude (Haiku)
        services.AddEmbeddingService(config);      // Supabase pgvector
        services.AddTelemetriaService(config);     // Supabase + Sentry — auditoria de operações de IA
        services.AddTracingService(config);         // Sentry Performance — quebra de tempo por página/vídeo
        services.AddObservabilidadeExterna(config); // Helicone — Sessions/Properties (resumo/categorização)
        services.AddChannelProcessor(config);

        // Um InstitutoPipelineHandler só, compartilhado por TODAS as interfaces de etapa (incluindo
        // IVideoHandler — dono da própria topologia de descoberta, sem FilaVideoHandler genérico).
        services.AddSingleton<InstitutoPipelineHandler>();
        services.AddSingleton<IVideoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
        services.AddSingleton<IEtapaTranscricaoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
        services.AddSingleton<IEtapaResumoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
        services.AddSingleton<IEtapaCategorizacaoHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());
        services.AddSingleton<IEtapaEmbeddingHandler>(sp => sp.GetRequiredService<InstitutoPipelineHandler>());

        // Filas + consumers de etapa (Feature #33 — US #41/#42/#43): Transcrição não é caso
        // especial, usa o mesmo mecanismo das demais. Resumo e Embedding dependem só de
        // Transcrição (fan-out), Categorização depende de Resumo. AddEtapaConsumer registra o
        // EtapaQueueConsumer pronto (keyed), sem montar a classe na mão no fim do arquivo.
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

// Feature #33 — 4 consumers (Transcrição/Resumo/Categorização/Embedding) rodando concorrentes,
// não em sequência: o objetivo é Resumo/Embedding começarem assim que o PRIMEIRO vídeo estiver
// transcrito, não esperar o lote inteiro da Transcrição terminar (isso anularia o ganho da US
// #41/#43). Transcrição não é caso especial — é só mais uma etapa keyed ("transcricao"), mesmo
// EtapaQueueConsumer genérico usado pelas demais — já pronto, registrado por AddEtapaConsumer.
await Task.WhenAll(
    host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("transcricao").ConsumirAsync(),
    host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("resumo").ConsumirAsync(),
    host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("categorizacao").ConsumirAsync(),
    host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("embedding").ConsumirAsync());

await host.StopAsync();
