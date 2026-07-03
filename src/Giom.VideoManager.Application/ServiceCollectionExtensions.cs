using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Application.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o pipeline de ingestão de vídeos (ChannelProcessor). O IVideoHandler concreto
    /// (Instituto/Ministério) é registrado pelo worker — é a única peça específica de canal.
    /// </summary>
    public static IServiceCollection AddChannelProcessor(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ProcessingOptions>(config.GetSection("ProcessingOptions"));
        services.AddSingleton<ChannelProcessor>();
        return services;
    }

    /// <summary>
    /// Registra o orquestrador genérico do bot de chat — não conhece canal nem LLM concretos.
    /// </summary>
    public static IServiceCollection AddChatBotProcessor(this IServiceCollection services)
    {
        services.AddSingleton<ChatBotProcessor>();
        return services;
    }
}
