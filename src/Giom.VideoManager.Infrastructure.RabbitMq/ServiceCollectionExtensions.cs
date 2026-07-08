using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.RabbitMq;

public static class ServiceCollectionExtensions
{
    /// <summary>Fila:RabbitMq — ConnectionString (CloudAMQP/LavinMQ, mesmo protocolo AMQP 0-9-1)/QueueName.</summary>
    public static IServiceCollection AddRabbitMqFila(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RabbitMqOptions>(config.GetSection("Fila:RabbitMq"));

        services.AddSingleton<IFilaPublisher<VideoParaProcessar>, RabbitMqFilaPublisher<VideoParaProcessar>>();
        services.AddSingleton<IFilaConsumer<VideoParaProcessar>, RabbitMqFilaConsumer<VideoParaProcessar>>();

        return services;
    }
}
