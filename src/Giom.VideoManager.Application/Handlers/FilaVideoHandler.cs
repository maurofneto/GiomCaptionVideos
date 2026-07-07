using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Application.Handlers;

/// <summary>
/// Decorator de IVideoHandler (US #39/#40) — em vez de processar o vídeo direto, publica na
/// fila como unidade de trabalho independente. IsRelevant delega pro IVideoProcessador real
/// (Instituto/Ministério) — o filtro de relevância continua o mesmo, só quem "processa" muda.
/// ChannelProcessor não sabe (nem precisa saber) que isso está acontecendo — é só mais um
/// IVideoHandler, igual sempre foi (Open/Closed: zero mudança em ChannelProcessor).
/// Quem de fato chama IVideoProcessador.HandleAsync é o VideoQueueConsumer, do outro lado da
/// fila (mesma execução — ver decisão de topologia da US #39/#40).
/// </summary>
public class FilaVideoHandler(IFilaPublisher<VideoParaProcessar> filaPublisher, IVideoProcessador processadorReal) : IVideoHandler
{
    public bool IsRelevant(Video video) => processadorReal.IsRelevant(video);

    public Task HandleAsync(Video video, CancellationToken ct = default) =>
        filaPublisher.PublicarAsync(new VideoParaProcessar(video), ct);
}
