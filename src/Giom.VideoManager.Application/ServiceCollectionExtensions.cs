using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Memoria;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o pipeline de ingestão de vídeos: ChannelProcessor descobre e publica na fila
    /// (via FilaVideoHandler, o IVideoHandler que injeta), VideoQueueConsumer drena e processa de
    /// verdade (via IVideoProcessador — US #39/#40). O worker registra só o IVideoProcessador
    /// concreto (Instituto/Ministério) e a fila (AddFilaVideo); o resto é genérico, daqui.
    /// </summary>
    public static IServiceCollection AddChannelProcessor(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ProcessingOptions>(config.GetSection("ProcessingOptions"));
        services.AddSingleton<IVideoHandler, FilaVideoHandler>();
        services.AddSingleton<ChannelProcessor>();
        services.AddSingleton<VideoQueueConsumer>();
        return services;
    }

    /// <summary>
    /// Registra o orquestrador genérico do bot de chat — não conhece canal nem LLM concretos.
    /// Não registra IConversaHistoricoService — quem compõe o worker escolhe o provider via
    /// AddConversaHistorico (Composition) ou registra um diretamente.
    /// </summary>
    public static IServiceCollection AddChatBotProcessor(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RagOptions>(config.GetSection("Rag"));
        services.Configure<DebugMetricasOptions>(config.GetSection("DebugMetricas"));
        services.Configure<HistoricoOptions>(config.GetSection("ConversaHistorico"));
        services.AddSingleton<ChatBotProcessor>();
        return services;
    }

    /// <summary>
    /// Comandos utilitários do bot (ajuda, sobre, limpar) — quem chama cada método, e como
    /// reconhece a invocação (ex: "/limpar" no Telegram), é responsabilidade de cada canal.
    /// </summary>
    public static IServiceCollection AddBotComandos(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BotOptions>(config.GetSection("Bot"));
        services.AddSingleton<IBotComandosService, BotComandosService>();
        return services;
    }

    /// <summary>
    /// Histórico de conversa em memória — some ao reiniciar o processo. Suficiente para
    /// desenvolvimento local ou baixo volume; trocar por Supabase/MongoDB em produção.
    /// </summary>
    public static IServiceCollection AddInMemoryConversaHistorico(this IServiceCollection services)
    {
        services.AddSingleton<IConversaHistoricoService, InMemoryConversaHistoricoService>();
        return services;
    }
}
