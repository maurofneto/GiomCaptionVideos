using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Sqs;

/// <summary>
/// Garante fila + DLQ a partir do QueueName, criando o que ainda não existir — a DLQ irmã
/// ({QueueName}-dlq) nasce primeiro, e a fila principal aponta pra ela via RedrivePolicy
/// (maxReceiveCount, US #44): depois de N receives sem confirmação, o próprio SQS move a
/// mensagem pra DLQ. CreateQueue é idempotente só quando os atributos batem; se a fila já
/// existe com atributos antigos (ex.: criada antes da RedrivePolicy existir), converge via
/// SetQueueAttributes em vez de falhar. Singleton — resolve/cria uma vez por processo e
/// cacheia, evitando uma chamada a mais por mensagem.
/// </summary>
public class SqsFilaCanal(IAmazonSQS cliente, IOptions<SqsOptions> options)
{
    private readonly SqsOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _queueUrl;

    /// <summary>Retenção da DLQ: 14 dias (máximo do SQS) — prazo pra investigar/reprocessar (Feature #35) antes de perder a mensagem.</summary>
    private const string RetencaoDlqSegundos = "1209600";

    public string NomeDlq => $"{_options.QueueName}-dlq";

    public async Task<string> ResolverQueueUrlAsync(CancellationToken ct)
    {
        if (_queueUrl is not null) return _queueUrl;

        await _lock.WaitAsync(ct);
        try
        {
            if (_queueUrl is not null) return _queueUrl;

            var dlqArn = await GarantirDlqAsync(ct);

            _queueUrl = await CriarOuConvergirAsync(_options.QueueName, new Dictionary<string, string>
            {
                [QueueAttributeName.VisibilityTimeout] = _options.VisibilityTimeout.ToString(),
                [QueueAttributeName.RedrivePolicy] = JsonSerializer.Serialize(new
                {
                    deadLetterTargetArn = dlqArn,
                    maxReceiveCount = _options.MaxReceiveCount.ToString()
                })
            }, ct);

            return _queueUrl;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> GarantirDlqAsync(CancellationToken ct)
    {
        var dlqUrl = await CriarOuConvergirAsync(NomeDlq, new Dictionary<string, string>
        {
            [QueueAttributeName.MessageRetentionPeriod] = RetencaoDlqSegundos
        }, ct);

        var atributos = await cliente.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            AttributeNames = [QueueAttributeName.QueueArn]
        }, ct);

        return atributos.QueueARN;
    }

    private async Task<string> CriarOuConvergirAsync(string nome, Dictionary<string, string> atributos, CancellationToken ct)
    {
        try
        {
            var criada = await cliente.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = nome,
                Attributes = atributos
            }, ct);
            return criada.QueueUrl;
        }
        catch (QueueNameExistsException)
        {
            // Fila existe com atributos diferentes dos pedidos — converge em vez de falhar
            var existente = await cliente.GetQueueUrlAsync(nome, ct);
            await cliente.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = existente.QueueUrl,
                Attributes = atributos
            }, ct);
            return existente.QueueUrl;
        }
    }
}
