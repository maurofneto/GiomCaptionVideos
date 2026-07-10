using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
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
public class SqsFilaCanal(IAmazonSQS cliente, IOptions<SqsOptions> options, ILogger<SqsFilaCanal> logger, TimeSpan? delayEntreTentativas = null)
{
    private readonly SqsOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _queueUrl;

    /// <summary>Retenção da DLQ: 14 dias (máximo do SQS) — prazo pra investigar/reprocessar (Feature #35) antes de perder a mensagem.</summary>
    private const string RetencaoDlqSegundos = "1209600";

    /// <summary>
    /// Bug #103 (incidente 2026-07-09): o plano de controle do SQS já devolveu, na mesma janela
    /// de segundos, "fila já existe" (CreateQueue) e "fila não existe" (GetQueueUrl) pra mesma
    /// fila — uma inconsistência transitória do lado da AWS, não um erro real. Sem retry, isso
    /// derrubava o processo inteiro no boot (e, por causa do Task.WhenAll — ver Bug #102 —,
    /// levava junto os outros consumers saudáveis). 3 tentativas com um delay curto cobre esse
    /// tipo de blip sem atrasar demais um boot saudável. Delay injetável (default 5s) só pra
    /// permitir testes rápidos — produção sempre usa o default.
    /// </summary>
    private const int MaxTentativasResolucao = 3;
    private readonly TimeSpan _delayEntreTentativas = delayEntreTentativas ?? TimeSpan.FromSeconds(5);

    public string NomeDlq => $"{_options.QueueName}-dlq";

    public async Task<string> ResolverQueueUrlAsync(CancellationToken ct)
    {
        if (_queueUrl is not null) return _queueUrl;

        await _lock.WaitAsync(ct);
        try
        {
            if (_queueUrl is not null) return _queueUrl;

            _queueUrl = await ComRetryAsync(async () =>
            {
                var dlqArn = await GarantirDlqAsync(ct);

                return await CriarOuConvergirAsync(_options.QueueName, new Dictionary<string, string>
                {
                    [QueueAttributeName.VisibilityTimeout] = _options.VisibilityTimeout.ToString(),
                    [QueueAttributeName.RedrivePolicy] = JsonSerializer.Serialize(new
                    {
                        deadLetterTargetArn = dlqArn,
                        maxReceiveCount = _options.MaxReceiveCount.ToString()
                    })
                }, ct);
            }, ct);

            return _queueUrl;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> ComRetryAsync(Func<Task<string>> acao, CancellationToken ct)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                return await acao();
            }
            catch (Exception ex) when (tentativa < MaxTentativasResolucao)
            {
                logger.LogWarning(ex, "Falha ao resolver/criar a fila {Fila} (tentativa {Tentativa}/{Max}) — tentando de novo em {Delay}s",
                    _options.QueueName, tentativa, MaxTentativasResolucao, _delayEntreTentativas.TotalSeconds);
                await Task.Delay(_delayEntreTentativas, ct);
            }
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
