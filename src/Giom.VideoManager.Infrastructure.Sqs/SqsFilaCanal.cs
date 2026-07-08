using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Sqs;

/// <summary>
/// Resolve o QueueUrl a partir do QueueName, criando a fila automaticamente se ela ainda não
/// existir — CreateQueue é idempotente (mesmo nome + mesmos atributos só devolve a URL da fila
/// já existente), então não há problema em chamar isso a cada start do processo. Mesma ideia do
/// RabbitMqCanal.QueueDeclareAsync, adaptada pro SQS. Singleton — resolve/cria uma vez por
/// processo e cacheia, evitando uma chamada a mais por mensagem.
/// </summary>
public class SqsFilaCanal(IAmazonSQS cliente, IOptions<SqsOptions> options)
{
    private readonly SqsOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _queueUrl;

    public async Task<string> ResolverQueueUrlAsync(CancellationToken ct)
    {
        if (_queueUrl is not null) return _queueUrl;

        await _lock.WaitAsync(ct);
        try
        {
            if (_queueUrl is not null) return _queueUrl;

            var resposta = await cliente.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = _options.QueueName,
                Attributes = new Dictionary<string, string>
                {
                    [QueueAttributeName.VisibilityTimeout] = _options.VisibilityTimeout.ToString()
                }
            }, ct);

            _queueUrl = resposta.QueueUrl;
            return _queueUrl;
        }
        finally
        {
            _lock.Release();
        }
    }
}
