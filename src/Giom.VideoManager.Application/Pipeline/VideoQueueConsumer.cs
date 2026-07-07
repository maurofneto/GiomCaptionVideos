using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Drena a fila de ingestão (US #39/#40) e processa cada vídeo via IVideoProcessador — o lado
/// consumidor da unidade de trabalho publicada por FilaVideoHandler (o IVideoHandler que
/// ChannelProcessor de fato injeta, ver Handlers/FilaVideoHandler.cs). Roda logo depois de
/// ChannelProcessor.RunAsync, na mesma execução (ver decisão de topologia da US #39/#40): zero
/// mudança de deploy agora, mas a Feature #33 (workers separados por etapa) pode rodar isso num
/// processo de longa duração depois sem tocar em interface nenhuma.
/// Reaplica a MESMA SessaoObservabilidade que estava ativa em ChannelProcessor no momento da
/// publicação (mensagem.Sessao) — sem isso, os atributos de Sentry/Helicone (Environment/Canal/
/// Playlist/Pagina) se perdiam, e o consumidor reconstruía uma sessão pobre. E abre o Span
/// "pipeline.video" com o traceparent capturado na publicação (mensagem.TraceParent) — sem
/// isso, o Span pai ("pipeline.pagina") já fechou pelo tempo que este código roda, e o novo Span
/// viraria uma Transaction raiz isolada em vez de filho, quebrando a árvore única no Sentry.
/// Se handler lançar, relança pro IFilaConsumer decidir a retentativa (visibility timeout no
/// SQS, nack+requeue no RabbitMQ) — não precisa de Dead-Letter Queue dedicada pra "não perder
/// trabalho por falha transitória" funcionar.
/// </summary>
public class VideoQueueConsumer(
    IFilaConsumer<VideoParaProcessar> filaConsumer,
    IVideoProcessador processadorReal,
    ITracingService tracingService,
    ISessionContext sessionContext,
    ILogger<VideoQueueConsumer> logger)
{
    public Task ConsumirAsync(CancellationToken ct = default) =>
        filaConsumer.ConsumirAsync(async (mensagem, token) =>
        {
            var video = mensagem.Video;

            sessionContext.Definir(mensagem.Sessao ?? new SessaoObservabilidade(
                SessionId: Guid.NewGuid().ToString(),
                SessionName: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
                SessionPath: $"/pipeline-consumer/{video.ChannelTitle}/{video.Id}",
                UserId: video.ChannelTitle,
                Propriedades: new Dictionary<string, string>
                {
                    ["Canal"] = video.ChannelTitle,
                    ["VideoId"] = video.Id,
                    ["VideoTitle"] = video.Title
                }));

            // "video.processamento", não "pipeline.video" de novo — evita duas transactions com o
            // mesmo nome aninhadas (ChannelProcessor já abriu "pipeline.video" pro enfileiramento;
            // este é o processamento de fato, filho daquele).
            using var traceVideo = tracingService.IniciarTrace("video.processamento", "video_pipeline", mensagem.TraceParent);

            logger.LogInformation("Consumindo vídeo {Id} — {Title} (canal {Canal})", video.Id, video.Title, video.ChannelTitle);

            try
            {
                await processadorReal.HandleAsync(video, token);
            }
            catch (Exception ex)
            {
                traceVideo.MarcarErro(ex);
                logger.LogError(ex, "Erro ao processar vídeo {Id} — {Title}", video.Id, video.Title);
                throw; // relança — o IFilaConsumer decide a retentativa (nack/requeue), não confirma (delete/ack) a mensagem
            }
        }, ct);
}
