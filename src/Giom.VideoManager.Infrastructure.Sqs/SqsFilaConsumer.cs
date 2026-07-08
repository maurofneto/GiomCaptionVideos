using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Sqs;

/// <summary>
/// Drena a fila (long polling) até um ReceiveMessage devolver vazio — condizente com "mesma
/// execução processa o que a descoberta publicou" (ver ChannelProcessor/EtapaQueueConsumer).
/// Mensagem só é confirmada (deletada) se o handler não lançar; se lançar, a mensagem some do
/// receive mas continua na fila — expira o VisibilityTimeout e o SQS reentrega sozinho, sem
/// precisar de nenhuma Dead-Letter Queue dedicada só pra isso funcionar.
/// </summary>
public class SqsFilaConsumer<TMensagem>(IAmazonSQS cliente, SqsFilaCanal canal, IOptions<SqsOptions> options, ILogger<SqsFilaConsumer<TMensagem>> logger) : IFilaConsumer<TMensagem>
{
    private readonly SqsOptions _options = options.Value;

    public async Task ConsumirAsync(Func<TMensagem, CancellationToken, Task> handler, CancellationToken ct = default)
    {
        var queueUrl = await canal.ResolverQueueUrlAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var resposta = await cliente.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = _options.MaxNumberOfMessages,
                WaitTimeSeconds = _options.WaitTimeSeconds,
                VisibilityTimeout = _options.VisibilityTimeout
            }, ct);

            if (resposta.Messages is not { Count: > 0 }) break;

            foreach (var mensagemSqs in resposta.Messages)
            {
                try
                {
                    var mensagem = JsonSerializer.Deserialize<TMensagem>(mensagemSqs.Body)
                        ?? throw new InvalidOperationException($"Mensagem SQS desserializou como null: {mensagemSqs.MessageId}");

                    await handler(mensagem, ct);

                    await cliente.DeleteMessageAsync(queueUrl, mensagemSqs.ReceiptHandle, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Falha ao processar mensagem SQS {MessageId} — deixando o VisibilityTimeout expirar pra reentrega", mensagemSqs.MessageId);
                }
            }
        }
    }
}
