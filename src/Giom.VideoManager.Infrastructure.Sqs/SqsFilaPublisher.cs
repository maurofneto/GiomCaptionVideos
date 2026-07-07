using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Sqs;

public class SqsFilaPublisher<TMensagem>(IAmazonSQS cliente, IOptions<SqsOptions> options) : IFilaPublisher<TMensagem>
{
    private readonly SqsOptions _options = options.Value;

    public async Task PublicarAsync(TMensagem mensagem, CancellationToken ct = default)
    {
        var corpo = JsonSerializer.Serialize(mensagem);
        await cliente.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _options.QueueUrl,
            MessageBody = corpo
        }, ct);
    }
}
