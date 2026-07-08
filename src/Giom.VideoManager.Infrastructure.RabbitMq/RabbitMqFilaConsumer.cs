using System.Text.Json;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Giom.VideoManager.Infrastructure.RabbitMq;

/// <summary>
/// Drena a fila via BasicGetAsync (pull, uma mensagem por vez) até devolver null — mesmo
/// contrato de "consome até a fila esvaziar" do SqsFilaConsumer, em vez do padrão push
/// (BasicConsumeAsync/AsyncEventingBasicConsumer) que ficaria escutando indefinidamente.
/// Mensagem só é confirmada (ack) se o handler não lançar; se lançar, nack com requeue: true —
/// RabbitMQ devolve a mensagem pra fila pra uma tentativa futura, sem precisar de DLQ dedicada.
/// </summary>
public class RabbitMqFilaConsumer<TMensagem>(IOptions<RabbitMqOptions> options, ILogger<RabbitMqFilaConsumer<TMensagem>> logger) : IFilaConsumer<TMensagem>
{
    private readonly RabbitMqOptions _options = options.Value;

    public async Task ConsumirAsync(Func<TMensagem, CancellationToken, Task> handler, CancellationToken ct = default)
    {
        using var sessao = await RabbitMqCanal.AbrirAsync(_options, ct);

        while (!ct.IsCancellationRequested)
        {
            var resultado = await sessao.Canal.BasicGetAsync(_options.QueueName, autoAck: false, ct);
            if (resultado is null) break;

            try
            {
                var mensagem = JsonSerializer.Deserialize<TMensagem>(resultado.Body.Span)
                    ?? throw new InvalidOperationException("Mensagem RabbitMQ desserializou como null");

                await handler(mensagem, ct);

                await sessao.Canal.BasicAckAsync(resultado.DeliveryTag, multiple: false, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao processar mensagem RabbitMQ (delivery tag {DeliveryTag}) — devolvendo pra fila (requeue)", resultado.DeliveryTag);
                await sessao.Canal.BasicNackAsync(resultado.DeliveryTag, multiple: false, requeue: true, ct);
            }
        }
    }
}
