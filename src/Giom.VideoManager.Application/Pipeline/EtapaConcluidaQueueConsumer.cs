using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Consumer da fila única de eventos de conclusão (Feature #104) — drena
/// <c>giom-videos-etapa-concluida-{env}</c> e delega pro PipelineHandler (orquestrador), que é
/// quem decide o que fazer com cada evento. Mesmo padrão de <see cref="EtapaQueueConsumer"/>
/// (sessão/traceparent reaplicados, span aberto por evento, exceção relançada — o IFilaConsumer
/// decide retry/DLQ), só que tipado pra EtapaConcluidaMensagem em vez de VideoEtapaMensagem — os
/// dois shapes carregam campos diferentes o suficiente (Etapa vs nenhum) pra não valer a pena
/// forçar um generic só por reuso.
/// </summary>
public class EtapaConcluidaQueueConsumer(
    IFilaConsumer<EtapaConcluidaMensagem> filaConsumer,
    Func<EtapaConcluidaMensagem, CancellationToken, Task> processar,
    ITracingService tracingService,
    ISessionContext sessionContext,
    ILogger<EtapaConcluidaQueueConsumer> logger)
{
    public Task ConsumirAsync(CancellationToken ct = default) =>
        filaConsumer.ConsumirAsync(async (mensagem, token) =>
        {
            var video = mensagem.Video;

            sessionContext.Definir(mensagem.Sessao ?? new SessaoObservabilidade(
                SessionId: Guid.NewGuid().ToString(),
                SessionName: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
                SessionPath: $"/orquestracao/{video.ChannelTitle}/{video.Id}",
                UserId: video.ChannelTitle,
                Propriedades: new Dictionary<string, string>
                {
                    ["Canal"] = video.ChannelTitle,
                    ["VideoId"] = video.Id,
                    ["VideoTitle"] = video.Title,
                    ["EtapaConcluida"] = mensagem.Etapa
                }));

            using var trace = tracingService.IniciarTrace("pipeline.orquestracao", "video_pipeline", mensagem.TraceParent);

            logger.LogInformation("Orquestrando — etapa {Etapa} concluída pra {Id} ({Title}, canal {Canal})",
                mensagem.Etapa, video.Id, video.Title, video.ChannelTitle);

            try
            {
                await processar(mensagem, token);
            }
            catch (Exception ex)
            {
                trace.MarcarErro(ex);
                logger.LogError(ex, "Erro ao orquestrar a etapa {Etapa} do vídeo {Id} — {Title}", mensagem.Etapa, video.Id, video.Title);
                throw;
            }
        }, ct);
}
