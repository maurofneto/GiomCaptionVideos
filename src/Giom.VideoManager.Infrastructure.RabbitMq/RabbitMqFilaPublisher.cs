using System.Text.Json;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Giom.VideoManager.Infrastructure.RabbitMq;

public class RabbitMqFilaPublisher<TMensagem>(IOptions<RabbitMqOptions> options) : IFilaPublisher<TMensagem>
{
    private readonly RabbitMqOptions _options = options.Value;

    public async Task PublicarAsync(TMensagem mensagem, CancellationToken ct = default)
    {
        using var sessao = await RabbitMqCanal.AbrirAsync(_options, ct);

        var corpo = JsonSerializer.SerializeToUtf8Bytes(mensagem);
        await sessao.Canal.BasicPublishAsync(exchange: string.Empty, routingKey: _options.QueueName, body: corpo, cancellationToken: ct);
    }
}
