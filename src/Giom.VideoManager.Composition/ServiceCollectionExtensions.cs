using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Memoria;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Anthropic;
using Giom.VideoManager.Infrastructure.Feedback;
using Giom.VideoManager.Infrastructure.Grok;
using Giom.VideoManager.Infrastructure.GuardRails;
using Giom.VideoManager.Infrastructure.Helicone;
using Giom.VideoManager.Infrastructure.Telemetria;
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
    /// Resumo — cadeia de fallback (Feature #101), configurável via Resumo:FallbackChain
    /// (default: Claude → Grok → OpenAI). Mesmo mecanismo de Keyed Service + FallbackChain de
    /// AddTranscriptionService — ver comentário lá. Valores aceitos: Claude, Grok, OpenAI,
    /// SummarizeTech.
    /// </summary>
    public static IServiceCollection AddSummaryService(this IServiceCollection services, IConfiguration config)
    {
        var cadeiaNomes = config.GetSection("Resumo:FallbackChain").Get<string[]>() ?? ["Claude", "Grok", "OpenAI"];

        foreach (var nome in cadeiaNomes)
        {
            switch (nome)
            {
                case "Grok":
                    services.AddGrokSummary(config);
                    services.AddKeyedSingleton<IResumoService>(nome, (sp, _) => sp.GetRequiredService<GrokResumoService>());
                    break;
                case "OpenAI":
                    services.AddOpenAiSummary(config);
                    services.AddKeyedSingleton<IResumoService>(nome, (sp, _) => sp.GetRequiredService<OpenAiResumoService>());
                    break;
                case "SummarizeTech":
                    services.AddSummarizeTechSummary();
                    services.AddKeyedSingleton<IResumoService>(nome, (sp, _) => sp.GetRequiredService<SummarizeTechResumoService>());
                    break;
                default: // Claude
                    services.AddClaudeSummary(config);
                    services.AddKeyedSingleton<IResumoService>(nome, (sp, _) => sp.GetRequiredService<ClaudeResumoService>());
                    break;
            }
        }

        services.AddSingleton<IResumoService>(sp =>
        {
            var cadeia = cadeiaNomes.Select(nome => (nome, sp.GetRequiredKeyedService<IResumoService>(nome))).ToList();
            return new FallbackResumoService(cadeia, sp.GetRequiredService<ILogger<FallbackResumoService>>());
        });
        services.Decorate<IResumoService, TelemetriaResumoService>();

        return services;
    }

    /// <summary>
    /// Categorização — cadeia de fallback (Feature #101), configurável via
    /// Categorizacao:FallbackChain (default: Claude → Grok → OpenAI). Mesmo mecanismo de
    /// AddSummaryService.
    /// </summary>
    public static IServiceCollection AddCategorizacaoService(this IServiceCollection services, IConfiguration config)
    {
        var cadeiaNomes = config.GetSection("Categorizacao:FallbackChain").Get<string[]>() ?? ["Claude", "Grok", "OpenAI"];

        foreach (var nome in cadeiaNomes)
        {
            switch (nome)
            {
                case "Grok":
                    services.AddGrokCategorizacao(config);
                    services.AddKeyedSingleton<ICategorizacaoService>(nome, (sp, _) => sp.GetRequiredService<GrokCategorizacaoService>());
                    break;
                case "OpenAI":
                    services.AddOpenAiCategorizacao(config);
                    services.AddKeyedSingleton<ICategorizacaoService>(nome, (sp, _) => sp.GetRequiredService<OpenAiCategorizacaoService>());
                    break;
                default: // Claude
                    services.AddClaudeCategorizacao(config);
                    services.AddKeyedSingleton<ICategorizacaoService>(nome, (sp, _) => sp.GetRequiredService<ClaudeCategorizacaoService>());
                    break;
            }
        }

        services.AddSingleton<ICategorizacaoService>(sp =>
        {
            var cadeia = cadeiaNomes.Select(nome => (nome, sp.GetRequiredKeyedService<ICategorizacaoService>(nome))).ToList();
            return new FallbackCategorizacaoService(cadeia, sp.GetRequiredService<ILogger<FallbackCategorizacaoService>>());
        });
        services.Decorate<ICategorizacaoService, TelemetriaCategorizacaoService>();

        return services;
    }

    /// <summary>Avaliador de conteúdo via IA (sempre Claude por enquanto) — usado pelo guardrail AvaliacaoIA.</summary>
    public static IServiceCollection AddAvaliadorIAService(this IServiceCollection services, IConfiguration config)
        => services.AddClaudeAvaliadorIA(config);

    /// <summary>
    /// Embedding — cadeia de fallback (Feature #101), configurável via Embedding:FallbackChain
    /// (default: só ["OpenAI"] por enquanto). Cada provider embeda a pergunta/os chunks do seu
    /// próprio lado e marca a origem (VideoChunk.Origem) — o Supabase (Edge Function
    /// buscar-similar) filtra por origem antes de comparar vetores, então adicionar um segundo
    /// provider na cadeia (ex: "VoyageAI") não mistura espaços vetoriais incompatíveis, ao
    /// contrário do que acontecia antes dessa mudança. Mesmo mecanismo de AddSummaryService.
    /// </summary>
    public static IServiceCollection AddEmbeddingService(this IServiceCollection services, IConfiguration config)
    {
        var cadeiaNomes = config.GetSection("Embedding:FallbackChain").Get<string[]>() ?? ["OpenAI"];

        foreach (var nome in cadeiaNomes)
        {
            switch (nome)
            {
                case "VoyageAI":
                    services.AddVoyageEmbedding(config);
                    services.AddKeyedSingleton<IEmbeddingService>(nome, (sp, _) => sp.GetRequiredService<VoyageEmbeddingService>());
                    break;
                default: // OpenAI
                    services.AddOpenAiEmbedding(config);
                    services.AddKeyedSingleton<IEmbeddingService>(nome, (sp, _) => sp.GetRequiredService<OpenAIEmbeddingService>());
                    break;
            }
        }

        services.AddSingleton<IEmbeddingService>(sp =>
        {
            var cadeia = cadeiaNomes.Select(nome => (nome, sp.GetRequiredKeyedService<IEmbeddingService>(nome))).ToList();
            return new FallbackEmbeddingService(cadeia, sp.GetRequiredService<ILogger<FallbackEmbeddingService>>());
        });
        services.Decorate<IEmbeddingService, TelemetriaEmbeddingService>();

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
