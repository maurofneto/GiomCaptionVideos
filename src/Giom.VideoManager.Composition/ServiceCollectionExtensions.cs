using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Memoria;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Anthropic;
using Giom.VideoManager.Infrastructure.Feedback;
using Giom.VideoManager.Infrastructure.GuardRails;
using Giom.VideoManager.Infrastructure.Helicone;
using Giom.VideoManager.Infrastructure.Telemetria;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Infrastructure.Mongo;
using Giom.VideoManager.Infrastructure.OpenAI;
using Giom.VideoManager.Infrastructure.Sql;
using Giom.VideoManager.Infrastructure.Supabase;
using Giom.VideoManager.Infrastructure.SummarizeTech;
using Giom.VideoManager.Infrastructure.Telegram;
using Giom.VideoManager.Infrastructure.Whisper;
using Giom.VideoManager.Infrastructure.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>Transcription:Provider — YouTubeTranscript (padrão), YouTubeCaption, YtDlpCaption, Whisper.</summary>
    public static IServiceCollection AddTranscriptionService(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Transcription:Provider"] ?? "YouTubeTranscript";

        return provider == "Whisper"
            ? services.AddWhisperTranscription(config)
            : services.AddYouTubeCaptionService(config);
    }

    /// <summary>Summary:Provider — Claude (padrão), SummarizeTech.</summary>
    public static IServiceCollection AddSummaryService(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Summary:Provider"] ?? "Claude";

        return provider == "SummarizeTech"
            ? services.AddSummarizeTechSummary()
            : services.AddClaudeSummary(config);
    }

    /// <summary>Categorização (sempre Claude por enquanto).</summary>
    public static IServiceCollection AddCategorizacaoService(this IServiceCollection services, IConfiguration config)
        => services.AddClaudeCategorizacao(config);

    /// <summary>Avaliador de conteúdo via IA (sempre Claude por enquanto) — usado pelo guardrail AvaliacaoIA.</summary>
    public static IServiceCollection AddAvaliadorIAService(this IServiceCollection services, IConfiguration config)
        => services.AddClaudeAvaliadorIA(config);

    /// <summary>
    /// Embedding:Provider — Direct (padrão), SupabaseEdgeFunction. IndexarAsync sai decorado
    /// com telemetria (tempo/custo — custo só aparece com Direct, que chama a OpenAI aqui
    /// mesmo; a Edge Function do Supabase chama a OpenAI do lado de lá, invisível pro .NET).
    /// </summary>
    public static IServiceCollection AddEmbeddingService(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Embedding:Provider"] ?? "Direct";

        if (provider == "SupabaseEdgeFunction")
            services.AddSupabaseEmbedding(config);
        else
            services.AddDirectEmbedding(config);

        services.Decorate<IEmbeddingService, TelemetriaEmbeddingService>();
        return services;
    }

    /// <summary>Agent de perguntas e respostas (sempre Claude por enquanto).</summary>
    public static IServiceCollection AddAgentService(this IServiceCollection services, IConfiguration config)
        => services.AddClaudeAgent(config);

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
    public static IServiceCollection AddTracingService(this IServiceCollection services)
        => services.AddSentryTracing();

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
    /// RespostaVazia (fase Saida) e AvaliacaoIA (fase configurável — usa IA em vez de regra
    /// fixa, ver GuardRailIAOptions:Instrucao).
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
            }
        }

        return services;
    }
}
