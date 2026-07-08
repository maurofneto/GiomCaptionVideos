using System.Net;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Sqs;

/// <summary>
/// Drena a fila (long polling) até um ReceiveMessage devolver vazio — condizente com "mesma
/// execução processa o que a descoberta publicou" (ver ChannelProcessor/EtapaQueueConsumer).
/// Mensagem só é confirmada (deletada) se o handler não lançar. Se lançar (US #44), o retry é
/// DINÂMICO pela exceção: falha transitória comum reduz a visibilidade pra RetryDelaySeconds
/// (menor que o long poll — a MESMA run tenta de novo); rate limit (RateLimitExcedidoException
/// dos adapters, ou HTTP 429 genérico) espera o Retry-After do provedor ou
/// RateLimitDelaySeconds — retry rápido contra cota estourada só queimaria tentativa. Na última
/// tentativa (ApproximateReceiveCount == MaxReceiveCount), loga Error (vira alerta no Sentry
/// via sink — US #45) e zera a visibilidade: o próximo receive dispara a RedrivePolicy e o SQS
/// move a mensagem pra DLQ agora, não na run do dia seguinte.
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
                VisibilityTimeout = _options.VisibilityTimeout,
                MessageSystemAttributeNames = [MessageSystemAttributeName.ApproximateReceiveCount]
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
                    await TratarFalhaAsync(queueUrl, mensagemSqs, ex, ct);
                }
            }
        }
    }

    private async Task TratarFalhaAsync(string queueUrl, Message mensagemSqs, Exception ex, CancellationToken ct)
    {
        var tentativa = LerTentativa(mensagemSqs);
        var ultimaTentativa = tentativa >= _options.MaxReceiveCount;
        var delay = CalcularDelayRetry(ex);

        if (ultimaTentativa)
            logger.LogError(ex, "Mensagem {MessageId} esgotou as {Max} tentativas na fila {Fila} — indo pra DLQ {Dlq}",
                mensagemSqs.MessageId, _options.MaxReceiveCount, _options.QueueName, canal.NomeDlq);
        else
            logger.LogWarning(ex, "Falha ({Motivo}) na tentativa {Tentativa}/{Max} da mensagem {MessageId} na fila {Fila} — retry em {Delay}s",
                delay == _options.RetryDelaySeconds ? "transitória" : "rate limit", tentativa, _options.MaxReceiveCount, mensagemSqs.MessageId, _options.QueueName, delay);

        try
        {
            // Última tentativa fica visível já (0s): o próximo receive dispara o move pra DLQ
            // dentro desta run. Se esta chamada falhar, o fallback é o comportamento antigo —
            // o VisibilityTimeout expira e o SQS reentrega na run seguinte.
            await cliente.ChangeMessageVisibilityAsync(queueUrl, mensagemSqs.ReceiptHandle, ultimaTentativa ? 0 : delay, ct);
        }
        catch (Exception exVisibilidade)
        {
            logger.LogWarning(exVisibilidade, "Falha ao reduzir a visibilidade da mensagem {MessageId} — retry fica pro VisibilityTimeout ({Timeout}s)",
                mensagemSqs.MessageId, _options.VisibilityTimeout);
        }
    }

    /// <summary>
    /// Retry dinâmico pela exceção: rate limit espera o que o provedor pedir (Retry-After) ou
    /// RateLimitDelaySeconds; o resto usa o retry rápido. Percorre a cadeia de InnerExceptions
    /// porque decorators (telemetria) e SDKs costumam embrulhar a causa original. O 429 via
    /// HttpRequestException cobre provedores que ainda não mapeiam pra RateLimitExcedidoException
    /// (ex.: Anthropic.SDK lança HttpRequestException com StatusCode). Teto de 43200s (12h) —
    /// máximo que o ChangeMessageVisibility do SQS aceita.
    /// </summary>
    private int CalcularDelayRetry(Exception ex)
    {
        for (var atual = ex; atual is not null; atual = atual.InnerException)
        {
            if (atual is RateLimitExcedidoException rateLimit)
                return (int)Math.Clamp(rateLimit.EsperaSugerida?.TotalSeconds ?? _options.RateLimitDelaySeconds, 1, 43200);

            if (atual is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                return Math.Clamp(_options.RateLimitDelaySeconds, 1, 43200);
        }

        return _options.RetryDelaySeconds;
    }

    private static int LerTentativa(Message mensagemSqs) =>
        mensagemSqs.Attributes is not null
        && mensagemSqs.Attributes.TryGetValue(MessageSystemAttributeName.ApproximateReceiveCount, out var valor)
        && int.TryParse(valor, out var tentativa)
            ? tentativa
            : 1;
}
