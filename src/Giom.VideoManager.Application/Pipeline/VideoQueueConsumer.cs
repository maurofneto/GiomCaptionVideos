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
/// Span "pipeline.video" aberto aqui (não mais em ChannelProcessor) — é aqui que HandleAsync de
/// fato roda. Se handler lançar, relança pro IFilaConsumer decidir a retentativa (visibility
/// timeout no SQS, nack+requeue no RabbitMQ) — não precisa de Dead-Letter Queue dedicada pra
/// "não perder trabalho por falha transitória" funcionar.
/// Reaproveita o CorrelationId (SessionId) que já estiver ambient — na topologia "mesma
/// execução", é o mesmo Guid que ChannelProcessor usou pra correlacionar toda a run.
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

            using var traceVideo = tracingService.IniciarTrace("pipeline.video", "video_pipeline");

            var correlationId = sessionContext.Atual?.SessionId ?? Guid.NewGuid().ToString();
            var horaExecucao = sessionContext.Atual?.SessionName ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

            sessionContext.Definir(new SessaoObservabilidade(
                SessionId: correlationId,
                SessionName: horaExecucao,
                SessionPath: $"/pipeline-consumer/{video.ChannelTitle}/{video.Id}",
                UserId: video.ChannelTitle,
                Propriedades: new Dictionary<string, string>
                {
                    ["Canal"] = video.ChannelTitle,
                    ["VideoId"] = video.Id,
                    ["VideoTitle"] = video.Title
                }));

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
