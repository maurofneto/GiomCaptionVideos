using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.RabbitMq;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra uma fila de etapa (Feature #33 — Transcrição/Resumo/Categorização/Embedding, todas
    /// tratadas igual, sem caso especial) como Keyed Service — RabbitMqFilaPublisher/Consumer abrem
    /// conexão própria por chamada (RabbitMqCanal), então não há singleton compartilhado pra
    /// colidir; só precisa de opções (ConnectionString/QueueName) distintas por chave. Genérico em
    /// <typeparamref name="TMensagem"/> desde a Feature #104 (fila de eventos de conclusão
    /// reaproveita o mesmo mecanismo, só muda o tipo da mensagem).
    /// </summary>
    public static IServiceCollection AddRabbitMqFilaEtapa<TMensagem>(this IServiceCollection services, string chave, IConfiguration config, string sectionPath)
    {
        var options = config.GetSection(sectionPath).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        var iOptions = Microsoft.Extensions.Options.Options.Create(options);

        services.AddKeyedSingleton<IFilaPublisher<TMensagem>>(chave, (_, _) =>
            new RabbitMqFilaPublisher<TMensagem>(iOptions));

        services.AddKeyedSingleton<IFilaConsumer<TMensagem>>(chave, (sp, _) =>
            new RabbitMqFilaConsumer<TMensagem>(iOptions, sp.GetRequiredService<ILogger<RabbitMqFilaConsumer<TMensagem>>>()));

        return services;
    }
}
