namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Publica uma mensagem numa fila — genérico sobre TMensagem de propósito, pra qualquer etapa
/// futura (ex: por-estágio da pipeline) poder reaproveitar o mesmo contrato/adapters (SQS,
/// RabbitMQ) sem precisar de código novo, só um tipo de mensagem novo.
/// </summary>
public interface IFilaPublisher<TMensagem>
{
    Task PublicarAsync(TMensagem mensagem, CancellationToken ct = default);
}
