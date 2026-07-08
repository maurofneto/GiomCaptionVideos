namespace Giom.VideoManager.Infrastructure.RabbitMq;

public class RabbitMqOptions
{
    /// <summary>URI AMQP completa (ex: amqps://usuario:senha@host/vhost) — mesmo formato que CloudAMQP/LavinMQ fornecem prontos no painel.</summary>
    public string ConnectionString { get; init; } = string.Empty;

    public string QueueName { get; init; } = "giom-videos-ingestao";
}
