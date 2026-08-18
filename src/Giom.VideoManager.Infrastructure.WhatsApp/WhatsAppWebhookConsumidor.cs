using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.WhatsApp;

/// <summary>
/// Consome a fila alimentada pela Lambda do webhook e entrega cada payload ao
/// <see cref="WhatsAppWebhookProcessor"/> (Task #140).
///
/// Por que existe um laço aqui em volta: <c>SqsFilaConsumer.ConsumirAsync</c> foi feito pros
/// workers de etapa, que são run-to-completion — ele DRENA a fila e retorna quando ela fica
/// vazia pelo grace period, deixando a task morrer e o autoscaling zerar. O bot é o oposto,
/// um serviço que fica de pé o tempo todo: sem o laço, ele pararia de escutar o WhatsApp
/// alguns minutos depois de subir e ninguém perceberia (o processo continua vivo por causa do
/// Telegram). O laço reabre a drenagem em vez de mexer no consumidor compartilhado, que está
/// em produção no pipeline de vídeos.
/// </summary>
public class WhatsAppWebhookConsumidor(
    IFilaConsumer<WhatsAppWebhookMensagem> fila,
    WhatsAppWebhookProcessor processador,
    ILogger<WhatsAppWebhookConsumidor> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _tempo = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Pausa entre uma drenagem e a próxima. Na prática o SqsFilaConsumer só volta depois de
    /// minutos (long polling + grace period), então isso quase nunca é sentido — mas sem a
    /// pausa, qualquer implementação de fila que retorne rápido (fila indisponível, fake em
    /// teste, mudança futura no consumidor) transformaria este laço num giro em vazio comendo
    /// CPU. Barato como proteção, invisível no caminho normal.
    /// </summary>
    private static readonly TimeSpan PausaEntreDrenagens = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan EsperaAposFalha = TimeSpan.FromSeconds(10);

    public async Task ExecutarAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Consumidor do webhook do WhatsApp iniciado");

        while (!ct.IsCancellationRequested)
        {
            var pausa = PausaEntreDrenagens;

            try
            {
                await fila.ConsumirAsync((mensagem, cancelamento) => processador.ProcessarAsync(mensagem.Payload, cancelamento), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Falha de infra (credencial, rede, fila sumiu). Não pode derrubar o processo:
                // o mesmo host atende o Telegram, que não tem nada a ver com isso.
                logger.LogError(ex, "Falha ao consumir a fila do webhook do WhatsApp — tentando de novo em {Segundos}s", EsperaAposFalha.TotalSeconds);
                pausa = EsperaAposFalha;
            }

            try
            {
                await Task.Delay(pausa, _tempo, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Consumidor do webhook do WhatsApp encerrado");
    }
}
