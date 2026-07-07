namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Consome mensagens de uma fila, chamando handler pra cada uma. Se handler lançar, a
/// implementação NÃO confirma a mensagem (delete/ack) — quem decide a política de retentativa é
/// o provider da fila (visibility timeout no SQS, nack+requeue no RabbitMQ), sem precisar de
/// nenhuma Dead-Letter Queue dedicada no código só pra isso funcionar.
/// </summary>
public interface IFilaConsumer<TMensagem>
{
    /// <summary>Consome até a fila esvaziar ou o CancellationToken ser cancelado (ver implementação concreta).</summary>
    Task ConsumirAsync(Func<TMensagem, CancellationToken, Task> handler, CancellationToken ct = default);
}
