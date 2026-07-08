using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Application.Handlers;

/// <summary>
/// Decorator de IVideoHandler (US #39/#40) — em vez de processar o vídeo direto, publica na
/// fila de descoberta (etapa Transcrição) como unidade de trabalho independente. IsRelevant
/// delega pro IVideoProcessador real (Instituto/Ministério) — o filtro de relevância continua o
/// mesmo, só quem "processa" muda. ChannelProcessor não sabe (nem precisa saber) que isso está
/// acontecendo — é só mais um IVideoHandler, igual sempre foi (Open/Closed: zero mudança em
/// ChannelProcessor).
/// Captura a sessão (ISessionContext.Atual, já definida por ChannelProcessor.DefinirSessaoAtual)
/// e o traceparent do Span "pipeline.video" corrente (ITracingService.CapturarContextoAtual) e
/// leva os dois junto na mensagem — sem isso, o consumidor do outro lado da fila perderia
/// atributos de sessão (Sentry/Helicone) e o Span de lá viraria uma trace isolada.
/// Quem de fato chama IEtapaTranscricaoHandler.ProcessarAsync é o EtapaQueueConsumer da etapa
/// Transcrição, do outro lado da fila (mesma execução — ver decisão de topologia da US #39/#40).
/// </summary>
public class FilaVideoHandler(
    IFilaPublisher<VideoEtapaMensagem> filaPublisher,
    IVideoProcessador processadorReal,
    ISessionContext sessionContext,
    ITracingService tracingService) : IVideoHandler
{
    public bool IsRelevant(Video video) => processadorReal.IsRelevant(video);

    public Task HandleAsync(Video video, CancellationToken ct = default)
    {
        var mensagem = new VideoEtapaMensagem(video, sessionContext.Atual, tracingService.CapturarContextoAtual());
        return filaPublisher.PublicarAsync(mensagem, ct);
    }
}
