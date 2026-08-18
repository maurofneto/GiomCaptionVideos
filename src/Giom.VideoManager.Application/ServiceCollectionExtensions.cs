using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Memoria;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o ChannelProcessor genérico — não registra IVideoHandler: cada canal é DONO da
    /// própria topologia de descoberta (Feature #33) e registra seu próprio
    /// InstitutoPipelineHandler/MinisterioPipelineHandler como IVideoHandler no Program.cs do
    /// worker, sem um decorator compartilhado (FilaVideoHandler) impondo o mesmo comportamento
    /// pra todo canal.
    /// </summary>
    public static IServiceCollection AddChannelProcessor(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ProcessingOptions>(config.GetSection("ProcessingOptions"));
        services.AddSingleton<ChannelProcessor>();
        return services;
    }

    /// <summary>
    /// Registra o EtapaQueueConsumer genérico (Feature #33) como Keyed Service, na MESMA chave da
    /// fila (ver AddFilaVideoEtapa) — reduz o Program.cs de cada worker a 1 linha por etapa, em vez
    /// de construir a classe na mão com 6 dependências repetidas a cada instância. Chame depois de
    /// AddFilaVideoEtapa(chave, ...) pra fila e o handler concreto (THandler) já registrados.
    /// </summary>
    public static IServiceCollection AddEtapaConsumer<THandler>(
        this IServiceCollection services,
        string chave,
        string nomeSpan,
        Func<THandler, Video, CancellationToken, Task> processar)
        where THandler : notnull
    {
        services.AddKeyedSingleton<EtapaQueueConsumer>(chave, (sp, key) => new EtapaQueueConsumer(
            sp.GetRequiredKeyedService<IFilaConsumer<VideoEtapaMensagem>>(key),
            (video, ct) => processar(sp.GetRequiredService<THandler>(), video, ct),
            nomeSpan,
            sp.GetRequiredService<ITracingService>(),
            sp.GetRequiredService<ISessionContext>(),
            sp.GetRequiredService<ILogger<EtapaQueueConsumer>>()));

        return services;
    }

    /// <summary>
    /// Registra o orquestrador central do pipeline (Feature #104) — PipelineHandler +
    /// EtapaConcluidaQueueConsumer, lendo a topologia por canal de Topologia:Canais/
    /// Topologia:Topologia. Único lugar que decide "o que vem depois de cada etapa" — nenhum
    /// worker de etapa sabe disso.
    /// </summary>
    public static IServiceCollection AddPipelineOrquestrador(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<Options.TopologiaOptions>(config.GetSection("Topologia"));
        services.AddSingleton<PipelineHandler>();
        // Sem factory: EtapaConcluidaQueueConsumer resolve a fila keyed via [FromKeyedServices]
        // no próprio construtor e depende do PipelineHandler diretamente (não de um Func) — o
        // container constrói sozinho, sem "new" aqui.
        services.AddSingleton<EtapaConcluidaQueueConsumer>();
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
        return services;
    }

    /// <summary>
    /// Sobe um ChatBotProcessor DEDICADO a um canal (US #24). Cada processor escuta o seu canal e
    /// mantém o próprio controle de "perguntas em andamento", então Telegram e WhatsApp rodam
    /// lado a lado sem um interferir no outro.
    ///
    /// ActivatorUtilities.CreateInstance resolve as outras dependências do construtor pelo
    /// container normalmente e só substitui o IChatChannel pelo canal desta chave. Sem isso
    /// seria preciso repetir a lista inteira de dependências aqui — e ela quebraria toda vez que
    /// o ChatBotProcessor ganhasse um parâmetro novo.
    /// </summary>
    public static IServiceCollection AddChatBotProcessorDoCanal(this IServiceCollection services, string chaveDoCanal)
    {
        services.AddKeyedSingleton(chaveDoCanal, (IServiceProvider sp, object? chave) =>
            ActivatorUtilities.CreateInstance<ChatBotProcessor>(sp, sp.GetRequiredKeyedService<IChatChannel>(chave)));

        services.AddSingleton(sp => new CanalDeChatAtivo(
            chaveDoCanal,
            ct => sp.GetRequiredKeyedService<ChatBotProcessor>(chaveDoCanal).RunAsync(ct)));

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
