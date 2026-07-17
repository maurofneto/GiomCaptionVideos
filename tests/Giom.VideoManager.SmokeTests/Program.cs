using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Categorizacao.Worker;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Embedding.Worker;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Infrastructure.Telemetria;
using Giom.VideoManager.SmokeTests;
using Giom.VideoManager.Resumo.Worker;
using Giom.VideoManager.Transcricao.Worker;
using Microsoft.Extensions.Logging;
using Serilog;

// Mesma composição do Giom.VideoManager.Instituto.Worker (Program.cs) — só troca
// AddVideoSourceService (YouTube real) pelo FakeVideoSource, e roda uma vez só.
// Objetivo: exercitar o pipeline real (resumo/categorização/embedding, Sentry, Helicone,
// CorrelationId) sem depender do YouTube nem de nenhuma chamada de IA — todo o CONTEÚDO de
// entrada (legenda/resumo/categoria/embedding) é 100% sintético (Fake*Service), sem nenhuma
// leitura no Supabase. A PERSISTÊNCIA continua indo pro Supabase de dev de verdade — é isso
// que valida span/telemetria/Helicone ponta a ponta. Cada Fake*Service injeta falha
// determinística por vídeo (ver InjecaoDeFalha) pra exercitar o retry dinâmico da Feature #34.
//
// Feature #104 — desde a orquestração central, este tool também exercita o fluxo distribuído
// real: publica na fila de Transcrição, os 4 workers de etapa (fakes por dentro) processam e
// avisam "concluí", e o PipelineHandler (mesmo orquestrador dos workers de produção) decide o
// fan-out — tudo dentro de 1 processo só, mas usando exatamente a mesma composição.
//
// US #106 — ChannelId REAL do Instituto/Ministério (antes "fake-instituto-teste"/
// "fake-ministerio-teste"): etapa_provedor_config tem FK pra canal_ingestao(channel_id), então
// só um ChannelId real resolve config por canal (prompt/UsarObservabilidade) de verdade — ver
// FakeVideoSource, que continua gerando vídeo/legenda 100% sintéticos (Video.Id = "teste-{guid}",
// nunca colide com dado real; filtrável depois por "id LIKE 'teste-%'").
var canaisDeTeste = new[]
{
    new CanalIngestaoConfig(FakeVideoSource.ChannelIdInstituto, null, "Instituto Giom (Teste E2E)", Mine: true, Ativo: true,
        RelevanciaTipo: RelevanciaTipo.LiveUnlisted, RelevanciaValores: [], MaxVideosPerRun: 5,
        DelayBetweenVideosMs: 0, DelayBetweenPagesMs: 0, SecretsKey: "teste-instituto"),
    new CanalIngestaoConfig(FakeVideoSource.ChannelIdMinisterio, null, "Ministério Giom (Teste E2E)", Mine: false, Ativo: true,
        RelevanciaTipo: RelevanciaTipo.TituloContem, RelevanciaValores: ["CULTO", "GIOM*", "MINISTÉRIO"], MaxVideosPerRun: 5,
        DelayBetweenVideosMs: 0, DelayBetweenPagesMs: 0, SecretsKey: "teste-ministerio"),
};

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

        services.AddPersistence(config);           // Supabase dev — só escrita real; conteúdo de entrada é 100% sintético (Fake*Service)
        services.AddSingleton<IVideoSourceService, FakeVideoSource>();
        // Decorado com TelemetriaCaptionService — igual AddYouTubeCaptionService faz pros
        // providers reais — senão a etapa "transcricao" nunca abre span/métrica nesta ferramenta.
        services.AddSingleton<FakeCaptionService>();
        services.AddSingleton<ICaptionService>(sp => new TelemetriaCaptionService(
            sp.GetRequiredService<FakeCaptionService>(),
            sp.GetRequiredService<ITelemetriaService>(),
            sp.GetRequiredService<ITracingService>(),
            sp.GetRequiredService<ILogger<TelemetriaCaptionService>>()));
        // Resumo: com Resumo:FallbackChain configurado, usa a cadeia REAL de fallback da
        // Feature #101 (Claude → Grok → OpenAI, mesma composição dos workers) em vez do Fake —
        // é assim que o E2E exercita o fallback de provider de verdade (ex.: ClaudeResumo:Model
        // inválido força a queda pro próximo elo). Sem a seção, mantém o Fake (retry/DLQ).
        if (config.GetSection("Resumo:FallbackChain").Exists())
            services.AddSummaryService(config);
        else
            services.AddSingleton<IResumoService, FakeResumoService>();
        // Categorização: mesmo mecanismo do Resumo acima — com Categorizacao:FallbackChain
        // configurado, usa a cadeia REAL (US #106, EtapaProvedorConfig por canal).
        if (config.GetSection("Categorizacao:FallbackChain").Exists())
            services.AddCategorizacaoService(config);
        else
            services.AddSingleton<ICategorizacaoService, FakeCategorizacaoService>();
        services.AddSingleton<IVideoIndexingService, FakeEmbeddingService>();
        services.AddSessionContext();
        services.AddActiveChannelContext(); // US #105 — ChannelProcessor agora exige isso no construtor; FakeVideoSource ignora, mas precisa estar registrado
        services.AddTelemetriaService(config);     // Supabase + Sentry + alerta de gasto
        services.AddTracingService(config);         // Sentry Performance
        services.AddObservabilidadeExterna(config); // Helicone Sessions/Properties
        services.AddChannelProcessor(config);

        // Ingestão — US #105: um CanalIngestaoVideoHandler por canal de teste (não mais
        // InstitutoPipelineHandler fixo), resolvido preguiçosamente igual AddIngestaoMultiCanal
        // faz em produção — só publica na fila de Transcrição (Feature #104).
        services.AddSingleton<Func<string, IVideoHandler>>(sp =>
        {
            var videoRepository = sp.GetRequiredService<IVideoRepository>();
            var transcricaoPublisher = sp.GetRequiredKeyedService<IFilaPublisher<VideoEtapaMensagem>>("transcricao");
            var sessionContext = sp.GetRequiredService<ISessionContext>();
            var tracingService = sp.GetRequiredService<ITracingService>();
            var logger = sp.GetRequiredService<ILogger<CanalIngestaoVideoHandler>>();
            var canalPorId = canaisDeTeste.ToDictionary(c => c.ChannelId);
            var cache = new Dictionary<string, IVideoHandler>();

            return channelId =>
            {
                if (!cache.TryGetValue(channelId, out var handler))
                {
                    handler = new CanalIngestaoVideoHandler(canalPorId[channelId], videoRepository, transcricaoPublisher, sessionContext, tracingService, logger);
                    cache[channelId] = handler;
                }
                return handler;
            };
        });

        // Handlers de etapa genéricos (mesmas classes dos workers de produção) — os Fake*Service
        // acima entram no lugar dos providers reais via injeção normal.
        services.AddSingleton<TranscricaoEtapaHandler>();
        services.AddSingleton<IEtapaTranscricaoHandler>(sp => sp.GetRequiredService<TranscricaoEtapaHandler>());
        services.AddSingleton<ResumoEtapaHandler>();
        services.AddSingleton<IEtapaResumoHandler>(sp => sp.GetRequiredService<ResumoEtapaHandler>());
        services.AddSingleton<CategorizacaoEtapaHandler>();
        services.AddSingleton<IEtapaCategorizacaoHandler>(sp => sp.GetRequiredService<CategorizacaoEtapaHandler>());
        services.AddSingleton<EmbeddingEtapaHandler>();
        services.AddSingleton<IEtapaEmbeddingHandler>(sp => sp.GetRequiredService<EmbeddingEtapaHandler>());

        // Filas de etapa + fila de eventos de conclusão + orquestrador (Feature #104) — mesma
        // composição de produção, só que todo mundo dentro do mesmo processo.
        services.AddFilaVideoEtapa<VideoEtapaMensagem>("transcricao", config, "FilaTranscricao");
        services.AddEtapaConsumer<IEtapaTranscricaoHandler>("transcricao", "video.transcricao", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa<VideoEtapaMensagem>("resumo", config, "FilaResumo");
        services.AddEtapaConsumer<IEtapaResumoHandler>("resumo", "video.resumo", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa<VideoEtapaMensagem>("categorizacao", config, "FilaCategorizacao");
        services.AddEtapaConsumer<IEtapaCategorizacaoHandler>("categorizacao", "video.categorizacao", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa<VideoEtapaMensagem>("embedding", config, "FilaEmbedding");
        services.AddEtapaConsumer<IEtapaEmbeddingHandler>("embedding", "video.embedding", (h, v, ct) => h.ProcessarAsync(v, ct));

        services.AddFilaVideoEtapa<EtapaConcluidaMensagem>("etapa-concluida", config, "FilaEtapaConcluida");
        services.AddPipelineOrquestrador(config);
    })
    .Build();

var canalDeTeste = canaisDeTeste.Select(c => new ChannelConfig(
    c.ChannelId, c.PlaylistId, c.Nome, c.Mine,
    c.MaxVideosPerRun, c.DelayBetweenVideosMs, c.DelayBetweenPagesMs)).ToArray();

// StartAsync/StopAsync (não só resolver os serviços direto) — sem isso o IHostedService que
// inicializa o Sentry/OpenTelemetry (registrado em AddSentryTracing) nunca roda, e nada é
// enviado pro Sentry (nem inicializado, nem descartado/flushado no fim).
await host.StartAsync();

// Torna visível qual perfil/modo de resumo esta run está usando — sem isso não dá pra saber, só
// pelo log, se a cadeia real de fallback (appsettings.teste-fallback.json) foi carregada ou não.
var configFinal = host.Services.GetRequiredService<IConfiguration>();
Log.Information("Perfil da run — DOTNET_ENVIRONMENT={Env}, Resumo={ModoResumo}, Categorizacao={ModoCategorizacao}, MaxVideosPerRun={Max}",
    Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "(nulo → Development)",
    configFinal.GetSection("Resumo:FallbackChain").Exists() ? $"fallback real [{string.Join("→", configFinal.GetSection("Resumo:FallbackChain").Get<string[]>() ?? [])}]" : "FakeResumoService",
    configFinal.GetSection("Categorizacao:FallbackChain").Exists() ? $"fallback real [{string.Join("→", configFinal.GetSection("Categorizacao:FallbackChain").Get<string[]>() ?? [])}]" : "FakeCategorizacaoService",
    configFinal["ProcessingOptions:MaxVideosPerRun"]);

var processor = host.Services.GetRequiredService<ChannelProcessor>();
await processor.RunAsync(canalDeTeste);

// Feature #104 — 4 workers de etapa + o orquestrador, concorrentes dentro do mesmo processo:
// conforme a Transcrição termina, publica "concluí" na fila de eventos; o orquestrador (que
// também está drenando, nesta mesma run) decide o fan-out; os workers seguintes pegam a
// mensagem nova assim que ela chega. Mesmo mecanismo run-to-completion de sempre — quando as 5
// filas ficam vazias, cada um sai sozinho.
await Task.WhenAll(
    host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("transcricao").ConsumirAsync(),
    host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("resumo").ConsumirAsync(),
    host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("categorizacao").ConsumirAsync(),
    host.Services.GetRequiredKeyedService<EtapaQueueConsumer>("embedding").ConsumirAsync(),
    host.Services.GetRequiredService<EtapaConcluidaQueueConsumer>().ConsumirAsync());

await host.StopAsync();

Log.Information("Execução de teste concluída — confira Sentry (Performance/Logs) e Helicone (Sessions) pra ver o resultado.");
await Log.CloseAndFlushAsync();
