using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Memoria;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Anthropic;
using Giom.VideoManager.Infrastructure.Cohere;
using Giom.VideoManager.Infrastructure.Feedback;
using Giom.VideoManager.Infrastructure.Grok;
using Giom.VideoManager.Infrastructure.GuardRails;
using Giom.VideoManager.Infrastructure.Helicone;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Giom.VideoManager.Infrastructure.Telemetria;
using Giom.VideoManager.Infrastructure.Redis;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Infrastructure.Mongo;
using Giom.VideoManager.Infrastructure.OpenAI;
using Giom.VideoManager.Infrastructure.RabbitMq;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Infrastructure.Sql;
using Giom.VideoManager.Infrastructure.Sqs;
using Giom.VideoManager.Infrastructure.Supabase;
using Giom.VideoManager.Infrastructure.SummarizeTech;
using Giom.VideoManager.Infrastructure.Telegram;
using Giom.VideoManager.Infrastructure.Voyage;
using Giom.VideoManager.Infrastructure.Whisper;
using Giom.VideoManager.Infrastructure.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Composition;

/// <summary>
/// Ponto único de composição: cada método decide, a partir de config, qual adapter
/// concreto usar e delega ao ServiceCollectionExtensions do próprio projeto adapter —
/// esta classe não conhece detalhes de implementação de nenhum provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Persistence:Provider — Supabase (padrão), MongoDB, SqlServer.</summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Persistence:Provider"] ?? "Supabase";

        return provider switch
        {
            "MongoDB" => services.AddMongoPersistence(config),
            "SqlServer" => services.AddSqlPersistence(config),
            _ => services.AddSupabasePersistence(config)
        };
    }

    /// <summary>
    /// Transcrição — cadeia de fallback (Feature #101), configurável via Transcricao:FallbackChain
    /// (default: YouTubeTranscript → YtDlpCaption → Whisper — mesma ordem de custo/quota de hoje,
    /// só sem parar no primeiro que não achar nada). Cada nome do array vira um provider registrado
    /// como Keyed Service; a cadeia tenta na ordem e só cai pro próximo em caso de falha/exceção
    /// (ver FallbackChain/FallbackHandler). Valores aceitos: YouTubeTranscript, YouTubeCaption,
    /// YtDlpCaption, Whisper.
    /// </summary>
    public static IServiceCollection AddTranscriptionService(this IServiceCollection services, IConfiguration config)
    {
        var cadeiaNomes = config.GetSection("Transcricao:FallbackChain").Get<string[]>()
            ?? ["YouTubeTranscript", "YtDlpCaption", "Whisper"];

        foreach (var nome in cadeiaNomes)
        {
            switch (nome)
            {
                case "YouTubeCaption":
                    // YouTubeCaptionService depende do YouTubeService (credencial OAuth) — não
                    // pressupõe que quem chamou AddTranscriptionService também chamou
                    // AddVideoSourceService (workers de etapa, ex: Transcricao.Worker, não
                    // descobrem vídeo, só processam a etapa).
                    services.AddYouTubeCredential(config);
                    services.Configure<YouTubeCaptionOptions>(config.GetSection("YouTubeCaption"));
                    services.AddKeyedSingleton<ICaptionService, YouTubeCaptionService>(nome);
                    break;
                case "YtDlpCaption":
                    services.Configure<YtDlpCaptionOptions>(config.GetSection("YtDlpCaption"));
                    services.AddKeyedSingleton<ICaptionService, YtDlpCaptionService>(nome);
                    break;
                case "Whisper":
                    services.Configure<WhisperOptions>(config.GetSection("Whisper"));
                    services.AddSingleton<ITranscricaoService, WhisperTranscricaoService>();
                    services.AddKeyedSingleton<ICaptionService, WhisperCaptionService>(nome);
                    break;
                default: // YouTubeTranscript
                    services.Configure<YouTubeTranscriptOptions>(config.GetSection("YouTubeTranscript"));
                    services.AddKeyedSingleton<ICaptionService, YouTubeTranscriptService>(nome);
                    break;
            }
        }

        services.AddSingleton<ICaptionService>(sp =>
        {
            var cadeia = cadeiaNomes.Select(nome => (nome, sp.GetRequiredKeyedService<ICaptionService>(nome))).ToList();
            return new FallbackCaptionService(cadeia, sp.GetRequiredService<ILogger<FallbackCaptionService>>());
        });
        services.Decorate<ICaptionService, TelemetriaCaptionService>();

        return services;
    }

    /// <summary>
    /// Resolve a cadeia de config de execução de IA por canal (US #106) — prompt +
    /// Model/MaxTokens/UsarObservabilidade + a ORDEM do fallback, tudo numa linha por
    /// (canal, etapa, provedor). Duas origens (EtapaProvedorConfig:Provider):
    /// "Repository" (default, Supabase) ou "Arquivo" (Prompts/*.md + FallbackChain do
    /// appsettings, o caminho de hoje). Decorada com cache — "Memory" (default, por processo,
    /// suficiente pros workers de etapa que drenam fila e saem) ou "Redis" (compartilhado entre
    /// instâncias, ver EtapaProvedorConfig:Cache:Redis). Idempotente — chamada por
    /// AddSummaryService e AddCategorizacaoService, que podem coexistir no mesmo worker.
    /// </summary>
    public static IServiceCollection AddEtapaProvedorConfigService(this IServiceCollection services, IConfiguration config)
    {
        if (services.Any(d => d.ServiceType == typeof(IEtapaProvedorConfigService)))
            return services;

        var provider = config["EtapaProvedorConfig:Provider"] ?? "Repository";
        if (provider == "Arquivo")
        {
            services.AddPromptLoader();
            services.AddSingleton<IEtapaProvedorConfigService, ArquivoEtapaProvedorConfigService>();
        }
        else
        {
            services.AddSupabaseEtapaProvedorConfig(config);
            services.AddSingleton<IEtapaProvedorConfigService, RepositorioEtapaProvedorConfigService>();
        }

        var cacheProvider = config["EtapaProvedorConfig:Cache:Provider"] ?? "Memory";
        if (cacheProvider == "Redis")
            services.AddRedisCacheEtapaProvedorConfig(config);
        else
            services.Decorate<IEtapaProvedorConfigService, CacheEtapaProvedorConfigService>();

        return services;
    }

    /// <summary>
    /// Resumo — cadeia de fallback por canal (US #106; antes Feature #101, cadeia global fixa
    /// via Resumo:FallbackChain). Cada provedor (Claude/Grok/OpenAI) é registrado Keyed como
    /// IResumoProvedorService — não decide mais sozinho seu prompt/config, recebe por parâmetro
    /// (ver ClaudeResumoService). FallbackResumoService (único IResumoService público) resolve a
    /// cadeia certa do canal em runtime via IEtapaProvedorConfigService. SummarizeTech fica de
    /// fora desta migração — não usa prompt/config por canal (nenhum canal ativo o referencia
    /// hoje) e continua só disponível se registrado manualmente noutro lugar, se algum dia
    /// reativado.
    /// </summary>
    public static IServiceCollection AddSummaryService(this IServiceCollection services, IConfiguration config)
    {
        services.AddEtapaProvedorConfigService(config);

        services.AddGrokSummary(config);
        services.AddKeyedSingleton<IResumoProvedorService>("Grok", (sp, _) => sp.GetRequiredService<GrokResumoService>());

        services.AddOpenAiSummary(config);
        services.AddKeyedSingleton<IResumoProvedorService>("OpenAI", (sp, _) => sp.GetRequiredService<OpenAiResumoService>());

        services.AddClaudeSummary(config);
        services.AddKeyedSingleton<IResumoProvedorService>("Claude", (sp, _) => sp.GetRequiredService<ClaudeResumoService>());

        services.AddSingleton<IResumoService, FallbackResumoService>();
        services.Decorate<IResumoService, TelemetriaResumoService>();

        return services;
    }

    /// <summary>
    /// Categorização — cadeia de fallback por canal (US #106). Mesmo mecanismo de
    /// AddSummaryService.
    /// </summary>
    public static IServiceCollection AddCategorizacaoService(this IServiceCollection services, IConfiguration config)
    {
        services.AddEtapaProvedorConfigService(config);

        services.AddGrokCategorizacao(config);
        services.AddKeyedSingleton<ICategorizacaoProvedorService>("Grok", (sp, _) => sp.GetRequiredService<GrokCategorizacaoService>());

        services.AddOpenAiCategorizacao(config);
        services.AddKeyedSingleton<ICategorizacaoProvedorService>("OpenAI", (sp, _) => sp.GetRequiredService<OpenAiCategorizacaoService>());

        services.AddClaudeCategorizacao(config);
        services.AddKeyedSingleton<ICategorizacaoProvedorService>("Claude", (sp, _) => sp.GetRequiredService<ClaudeCategorizacaoService>());

        services.AddSingleton<ICategorizacaoService, FallbackCategorizacaoService>();
        services.Decorate<ICategorizacaoService, TelemetriaCategorizacaoService>();

        return services;
    }

    /// <summary>Avaliador de conteúdo via IA (sempre Claude por enquanto) — usado pelo guardrail AvaliacaoIA.</summary>
    public static IServiceCollection AddAvaliadorIAService(this IServiceCollection services, IConfiguration config)
        => services.AddClaudeAvaliadorIA(config);

    /// <summary>
    /// Embedding — configurável via Embedding:FallbackChain (default: só ["OpenAI"] por
    /// enquanto; "VoyageAI"/"Cohere" também disponíveis). Cada provider embeda a pergunta/os
    /// chunks do seu próprio lado e marca a origem (VideoChunk.Origem) — a busca
    /// (IVideoChunkRepository.BuscarSimilaresAsync, Domain) filtra por origem antes de comparar
    /// vetores, então adicionar um provider na cadeia não mistura espaços vetoriais
    /// incompatíveis. Mesmo mecanismo de AddSummaryService.
    ///
    /// Indexação (IVideoIndexingService) e busca (IBuscaSemanticaService) são interfaces
    /// separadas (Feature #115, ISP) resolvidas SEMPRE pro mesmo combinator — sem config, sem
    /// troca em runtime: PipelineEmbeddingService pra indexar (roda todos os providers) e
    /// FallbackEmbeddingService pra buscar (para no primeiro que funcionar). Cada consumidor
    /// (EmbeddingEtapaHandler indexa, ChatBotProcessor busca) injeta só a interface que usa.
    /// </summary>
    public static IServiceCollection AddEmbeddingService(this IServiceCollection services, IConfiguration config)
    {
        var cadeiaNomes = config.GetSection("Embedding:FallbackChain").Get<string[]>() ?? ["OpenAI"];

        foreach (var nome in cadeiaNomes)
        {
            switch (nome)
            {
                case "VoyageAI":
                    services.AddSupabaseFarolRateLimiter(config); // Bug #122 — idempotente, seguro chamar de novo se Cohere também estiver na cadeia
                    services.AddVoyageEmbedding(config);
                    services.AddKeyedSingleton<IEmbeddingService>(nome, (sp, _) => sp.GetRequiredService<VoyageEmbeddingService>());
                    break;
                case "Cohere":
                    services.AddSupabaseFarolRateLimiter(config); // Bug #122
                    services.AddCohereEmbedding(config);
                    services.AddKeyedSingleton<IEmbeddingService>(nome, (sp, _) => sp.GetRequiredService<CohereEmbeddingService>());
                    break;
                default: // OpenAI
                    services.AddOpenAiEmbedding(config);
                    services.AddKeyedSingleton<IEmbeddingService>(nome, (sp, _) => sp.GetRequiredService<OpenAIEmbeddingService>());
                    break;
            }
        }

        services.AddSingleton<IBuscaSemanticaService>(sp =>
        {
            var cadeia = cadeiaNomes.Select(nome => (nome, sp.GetRequiredKeyedService<IEmbeddingService>(nome))).ToList();
            return new FallbackEmbeddingService(cadeia, sp.GetRequiredService<ILogger<FallbackEmbeddingService>>());
        });
        services.Decorate<IBuscaSemanticaService, TelemetriaBuscaSemanticaService>();

        services.AddSingleton<IVideoIndexingService>(sp =>
        {
            var cadeia = cadeiaNomes.Select(nome => (nome, sp.GetRequiredKeyedService<IEmbeddingService>(nome))).ToList();
            return new PipelineEmbeddingService(cadeia, sp.GetRequiredService<ILogger<PipelineEmbeddingService>>());
        });
        services.Decorate<IVideoIndexingService, TelemetriaVideoIndexingService>();

        return services;
    }

    /// <summary>
    /// Agent de perguntas e respostas — cadeia de fallback (Feature #101), configurável via
    /// Agent:FallbackChain (default: Claude → Grok → OpenAI). Mesmo mecanismo de
    /// AddSummaryService.
    /// </summary>
    public static IServiceCollection AddAgentService(this IServiceCollection services, IConfiguration config)
    {
        var cadeiaNomes = config.GetSection("Agent:FallbackChain").Get<string[]>() ?? ["Claude", "Grok", "OpenAI"];

        foreach (var nome in cadeiaNomes)
        {
            switch (nome)
            {
                case "Grok":
                    services.AddGrokAgent(config);
                    services.AddKeyedSingleton<IAgentService>(nome, (sp, _) => sp.GetRequiredService<GrokAgentService>());
                    break;
                case "OpenAI":
                    services.AddOpenAiAgent(config);
                    services.AddKeyedSingleton<IAgentService>(nome, (sp, _) => sp.GetRequiredService<OpenAiAgentService>());
                    break;
                default: // Claude
                    services.AddClaudeAgent(config);
                    services.AddKeyedSingleton<IAgentService>(nome, (sp, _) => sp.GetRequiredService<ClaudeAgentService>());
                    break;
            }
        }

        services.AddSingleton<IAgentService>(sp =>
        {
            var cadeia = cadeiaNomes.Select(nome => (nome, sp.GetRequiredKeyedService<IAgentService>(nome))).ToList();
            return new FallbackAgentService(cadeia, sp.GetRequiredService<ILogger<FallbackAgentService>>());
        });
        services.Decorate<IAgentService, TelemetriaAgentService>();

        return services;
    }

    /// <summary>Channel:Provider — Telegram (único por enquanto; WhatsApp/Web futuramente).</summary>
    public static IServiceCollection AddChatChannel(this IServiceCollection services, IConfiguration config)
        => services.AddTelegramChatChannel(config);

    /// <summary>VideoSource:Provider — YouTube (padrão, único por enquanto).</summary>
    public static IServiceCollection AddVideoSourceService(this IServiceCollection services, IConfiguration config)
        => services.AddYouTubeVideoSource(config);

    /// <summary>
    /// Bootstrap multi-canal pra ingestão unificada (US #105, Ingestao.Worker) — lê os canais
    /// ativos do Supabase (tabela canal_ingestao) SÍNCRONO durante ConfigureServices (mesmo
    /// padrão já aceito nesse código pra GetGoogleCredential(...).GetAwaiter().GetResult() em
    /// AddYouTubeCredential), registra um YouTubeService keyed por canal e um
    /// Func&lt;string, IVideoHandler&gt; resolvido preguiçosamente na primeira chamada — só
    /// depois que o container termina de montar, já que CanalIngestaoVideoHandler precisa de
    /// dependências registradas por OUTROS Add* nesta mesma composição (AddFilaVideoEtapa,
    /// AddTelemetriaService, AddTracingService), que podem rodar antes ou depois deste método.
    /// Composition não conhece SupabaseCanalIngestaoRepository nem GoogleCredentialOptions/
    /// YouTubeService diretamente — só a abstração ICanalIngestaoRepository (registrada por
    /// AddSupabaseCanalIngestao) e o método do adapter YouTube que encapsula toda a credencial
    /// (AddYouTubeCredenciaisPorCanal). Como a lista de canais precisa existir ANTES do
    /// container terminar de montar (host.Services ainda não existe neste ponto), resolve
    /// ICanalIngestaoRepository via um ServiceProvider temporário só pra essa leitura pontual —
    /// o registro real (usado pelo container final) continua vindo de AddSupabaseCanalIngestao.
    /// </summary>
    public static IServiceCollection AddIngestaoMultiCanal(this IServiceCollection services, IConfiguration config)
    {
        services.AddSupabaseCanalIngestao(config);

        IReadOnlyList<CanalIngestaoConfig> canaisValidos;
        using (var bootstrapProvider = services.BuildServiceProvider())
        {
            var canalRepository = bootstrapProvider.GetRequiredService<ICanalIngestaoRepository>();
            var canais = canalRepository.GetCanaisAtivosAsync().GetAwaiter().GetResult();

            // Logger vem do mesmo provider de bootstrap (já tem Serilog+Sentry configurados por
            // UseSerilog, registrado antes deste ConfigureServices no Host builder) — nada de
            // Console.WriteLine pra erro de credencial por canal. Resolvido e usado dentro do
            // using: o logger não precisa sobreviver ao descarte do provider temporário, só
            // durante esta chamada síncrona.
            var logger = bootstrapProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Giom.VideoManager.Composition.AddIngestaoMultiCanal");
            canaisValidos = services.AddYouTubeCredenciaisPorCanal(config, canais, logger);
        }

        services.AddSingleton<IReadOnlyList<CanalIngestaoConfig>>(canaisValidos);

        services.AddSingleton<Func<string, IVideoHandler>>(sp =>
        {
            var videoRepository = sp.GetRequiredService<IVideoRepository>();
            var transcricaoPublisher = sp.GetRequiredKeyedService<IFilaPublisher<VideoEtapaMensagem>>("transcricao");
            var sessionContext = sp.GetRequiredService<ISessionContext>();
            var tracingService = sp.GetRequiredService<ITracingService>();
            var canalPorId = canaisValidos.ToDictionary(c => c.ChannelId);
            var cache = new Dictionary<string, IVideoHandler>();

            return channelId =>
            {
                if (!cache.TryGetValue(channelId, out var handler))
                {
                    handler = new CanalIngestaoVideoHandler(canalPorId[channelId], videoRepository, transcricaoPublisher, sessionContext, tracingService);
                    cache[channelId] = handler;
                }
                return handler;
            };
        });

        return services;
    }

    /// <summary>
    /// ConversaHistorico:Provider — InMemory (padrão), Supabase, MongoDB. Decorado com
    /// FeedbackExternoConversaHistoricoService — reenvia o voto 👍/👎 pra um serviço de feedback
    /// externo (Helicone) sem que o canal (Telegram, etc.) precise saber que isso existe.
    /// </summary>
    public static IServiceCollection AddConversaHistorico(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["ConversaHistorico:Provider"] ?? "InMemory";

        switch (provider)
        {
            case "Supabase":
                services.AddSupabaseConversaHistorico(config);
                break;
            case "MongoDB":
                services.AddMongoConversaHistorico(config);
                break;
            default:
                services.AddInMemoryConversaHistorico();
                break;
        }

        services.Decorate<IConversaHistoricoService, FeedbackExternoConversaHistoricoService>();
        return services;
    }

    /// <summary>
    /// Observabilidade externa de sessões/usuários/propriedades e feedback (hoje via Helicone,
    /// mas plugável — Claude*Service e o histórico de conversa só conhecem ISessionContext e
    /// IFeedbackExternoService, nunca o provedor concreto). Chamado explicitamente por cada
    /// worker que usa Claude (Agent/Resumo/Categorização) ou feedback (bot).
    /// </summary>
    public static IServiceCollection AddObservabilidadeExterna(this IServiceCollection services, IConfiguration config)
        => services.AddHelicone(config);

    /// <summary>
    /// Sinks de telemetria — hoje Supabase (interacao_log) + Sentry (breadcrumb) + alerta de gasto
    /// diário. Cada RegistrarAsync chega em TODOS via CompositeTelemetriaService; adicionar um novo
    /// sink no futuro é só registrar o tipo concreto aqui e incluir na lista.
    /// </summary>
    public static IServiceCollection AddTelemetriaService(this IServiceCollection services, IConfiguration config)
    {
        services.AddSupabaseTelemetria(config);
        services.AddSentryTelemetria();
        services.Configure<AlertaGastoOptions>(config.GetSection("AlertaGasto"));
        services.AddSingleton<ITelemetriaSink, AlertaGastoTelemetriaService>();

        // CompositeTelemetriaService recebe todo ITelemetriaSink registrado via
        // IEnumerable<ITelemetriaSink> — adicionar um sink novo no futuro é só implementar
        // ITelemetriaSink e registrar aqui, nada muda no Composite.
        services.AddSingleton<ITelemetriaService, CompositeTelemetriaService>();
        return services;
    }

    /// <summary>Rastreamento de etapas de uma interação (ex: busca RAG / agente / envio) — hoje via Sentry Performance.</summary>
    public static IServiceCollection AddTracingService(this IServiceCollection services, IConfiguration config)
        => services.AddSentryTracing(config);

    /// <summary>
    /// Fila de etapa (Feature #33 — Transcrição/Resumo/Categorização/Embedding, todas tratadas
    /// igual, sem caso especial) — registrada como Keyed Service (<paramref name="chave"/>, ex:
    /// "transcricao"/"resumo"), provider decidido dentro da própria seção raiz
    /// (<paramref name="secaoRaiz"/>:Provider, ex: "FilaTranscricao:Provider"/"FilaResumo:Provider").
    /// Genérico em <typeparamref name="TMensagem"/> desde a Feature #104 — a fila de eventos de
    /// conclusão (<c>EtapaConcluidaMensagem</c>) reaproveita este mesmo método com o tipo trocado.
    /// Cada worker de etapa chama isso só pra própria fila (uma etapa, um worker) — não mais um
    /// canal chamando várias vezes pras etapas que usa (ver Instituto.Worker/Ministerio.Worker,
    /// que agora só chamam pra "transcricao").
    /// </summary>
    public static IServiceCollection AddFilaVideoEtapa<TMensagem>(this IServiceCollection services, string chave, IConfiguration config, string secaoRaiz)
    {
        var provider = config[$"{secaoRaiz}:Provider"] ?? "Sqs";
        return provider == "RabbitMq"
            ? services.AddRabbitMqFilaEtapa<TMensagem>(chave, config, $"{secaoRaiz}:RabbitMq")
            : services.AddSqsFilaEtapa<TMensagem>(chave, config, $"{secaoRaiz}:Sqs");
    }

    /// <summary>
    /// IncertezaDetector:Provider — Heuristica (padrão). Deixado plugável (switch, não uma
    /// ligação direta) porque a forma de detectar incerteza numa resposta pode virar um
    /// classificador de IA ou outra lib no futuro, sem afetar quem consome o resultado.
    /// </summary>
    public static IServiceCollection AddIncertezaDetectorService(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["IncertezaDetector:Provider"] ?? "Heuristica";

        return provider switch
        {
            _ => services.AddHeuristicaIncertezaDetector()
        };
    }

    /// <summary>
    /// GuardRails:Habilitados — lista de nomes do catálogo abaixo que roda em toda interação do
    /// bot (Segurança e Confiabilidade). Adicionar/remover um guardrail em produção é só editar
    /// essa lista no appsettings, sem mudar nem reimplantar código. Catálogo atual:
    /// PalavrasBloqueadas, PromptInjection, LimiteTamanhoMensagem, RateLimit (fase Entrada),
    /// RespostaVazia (fase Saida), AvaliacaoIA (fase configurável — usa IA em vez de regra fixa,
    /// ver GuardRailIAOptions:Instrucao) e AcessoRestrito (Board #22 — allowlist de chatIds;
    /// fica de fora de Habilitados por padrão, é opt-in do Mauro quando quiser restringir).
    /// </summary>
    public static IServiceCollection AddGuardRails(this IServiceCollection services, IConfiguration config)
    {
        services.AddGuardRailPipeline();

        var habilitados = config.GetSection("GuardRails:Habilitados").Get<string[]>() ?? [];

        foreach (var nome in habilitados)
        {
            switch (nome)
            {
                case "PalavrasBloqueadas":
                    services.Configure<PalavrasBloqueadasOptions>(config.GetSection("GuardRails:PalavrasBloqueadas"));
                    services.AddSingleton<IGuardRail, PalavrasBloqueadasGuardRail>();
                    break;
                case "PromptInjection":
                    services.AddSingleton<IGuardRail, PromptInjectionGuardRail>();
                    break;
                case "LimiteTamanhoMensagem":
                    services.Configure<LimiteTamanhoMensagemOptions>(config.GetSection("GuardRails:LimiteTamanhoMensagem"));
                    services.AddSingleton<IGuardRail, LimiteTamanhoMensagemGuardRail>();
                    break;
                case "RateLimit":
                    services.Configure<RateLimitOptions>(config.GetSection("GuardRails:RateLimit"));
                    services.AddSingleton<IGuardRail, RateLimitGuardRail>();
                    break;
                case "RespostaVazia":
                    services.AddSingleton<IGuardRail, RespostaVaziaGuardRail>();
                    break;
                case "AvaliacaoIA":
                    services.AddAvaliadorIAService(config);
                    services.Configure<GuardRailIAOptions>(config.GetSection("GuardRails:AvaliacaoIA"));
                    services.AddSingleton<IGuardRail, GuardRailIA>();
                    break;
                case "AcessoRestrito":
                    services.Configure<AllowlistControleAcessoOptions>(config.GetSection("GuardRails:AcessoRestrito"));
                    services.Configure<AcessoRestritoOptions>(config.GetSection("GuardRails:AcessoRestrito"));
                    services.AddSingleton<IControleAcessoService, AllowlistControleAcessoService>();
                    services.AddSingleton<IGuardRail, AcessoRestritoGuardRail>();
                    break;
            }
        }

        return services;
    }
}
