using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Sqs;

public class SqsFilaPublisher<TMensagem>(IAmazonSQS cliente, SqsFilaCanal canal) : IFilaPublisher<TMensagem>
{
    public async Task PublicarAsync(TMensagem mensagem, CancellationToken ct = default)
    {
        var queueUrl = await canal.ResolverQueueUrlAsync(ct);
        var corpo = JsonSerializer.Serialize(mensagem);
        await cliente.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = corpo
        }, ct);
    }
}
