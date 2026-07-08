using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.RabbitMq;

public static class ServiceCollectionExtensions
{
    /// <summary>Fila:RabbitMq — ConnectionString (CloudAMQP/LavinMQ, mesmo protocolo AMQP 0-9-1)/QueueName. Fila de descoberta/Transcrição (Feature #33 trata Transcrição como só mais uma etapa).</summary>
    public static IServiceCollection AddRabbitMqFila(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RabbitMqOptions>(config.GetSection("Fila:RabbitMq"));

        services.AddSingleton<IFilaPublisher<VideoEtapaMensagem>, RabbitMqFilaPublisher<VideoEtapaMensagem>>();
        services.AddSingleton<IFilaConsumer<VideoEtapaMensagem>, RabbitMqFilaConsumer<VideoEtapaMensagem>>();

        return services;
    }

    /// <summary>
    /// Registra uma fila de etapa (Feature #33 — Resumo/Categorização/Embedding) como Keyed
    /// Service — RabbitMqFilaPublisher/Consumer abrem conexão própria por chamada (RabbitMqCanal),
    /// então não há singleton compartilhado pra colidir; só precisa de opções (ConnectionString/
    /// QueueName) distintas por chave.
    /// </summary>
    public static IServiceCollection AddRabbitMqFilaEtapa(this IServiceCollection services, string chave, IConfiguration config, string sectionPath)
    {
        var options = config.GetSection(sectionPath).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        var iOptions = Microsoft.Extensions.Options.Options.Create(options);

        services.AddKeyedSingleton<IFilaPublisher<VideoEtapaMensagem>>(chave, (_, _) =>
            new RabbitMqFilaPublisher<VideoEtapaMensagem>(iOptions));

        services.AddKeyedSingleton<IFilaConsumer<VideoEtapaMensagem>>(chave, (sp, _) =>
            new RabbitMqFilaConsumer<VideoEtapaMensagem>(iOptions, sp.GetRequiredService<ILogger<RabbitMqFilaConsumer<VideoEtapaMensagem>>>()));

        return services;
    }
}
