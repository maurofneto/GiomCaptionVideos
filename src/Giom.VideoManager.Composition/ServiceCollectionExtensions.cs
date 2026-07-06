using Giom.VideoManager.Application;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Anthropic;
using Giom.VideoManager.Infrastructure.Telemetria;
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

    /// <summary>Embedding:Provider — Direct (padrão), SupabaseEdgeFunction.</summary>
    public static IServiceCollection AddEmbeddingService(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Embedding:Provider"] ?? "Direct";

        return provider == "SupabaseEdgeFunction"
            ? services.AddSupabaseEmbedding(config)
            : services.AddDirectEmbedding(config);
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

    /// <summary>ConversaHistorico:Provider — InMemory (padrão), Supabase, MongoDB.</summary>
    public static IServiceCollection AddConversaHistorico(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["ConversaHistorico:Provider"] ?? "InMemory";

        return provider switch
        {
            "Supabase" => services.AddSupabaseConversaHistorico(config),
            "MongoDB" => services.AddMongoConversaHistorico(config),
            _ => services.AddInMemoryConversaHistorico()
        };
    }

    /// <summary>
    /// Sinks de telemetria — hoje Supabase (interacao_log) + Sentry (breadcrumb). Cada
    /// RegistrarAsync chega em TODOS via CompositeTelemetriaService; adicionar um novo sink no
    /// futuro é só registrar o tipo concreto aqui e incluir na lista.
    /// </summary>
    public static IServiceCollection AddTelemetriaService(this IServiceCollection services, IConfiguration config)
    {
        services.AddSupabaseTelemetria(config);
        services.AddSingleton<SentryTelemetriaService>();
        services.AddSingleton<ITelemetriaService>(sp => new CompositeTelemetriaService(
            [sp.GetRequiredService<SupabaseTelemetriaService>(), sp.GetRequiredService<SentryTelemetriaService>()],
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CompositeTelemetriaService>>()));
        return services;
    }
}
