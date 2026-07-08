using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Consumer genérico reaproveitado por TODAS as 4 etapas do pipeline (Feature #33) — Transcrição
/// não é caso especial, é só mais uma instância deste mesmo consumer, drenando a fila de
/// descoberta (não-keyed) em vez de uma fila de etapa (keyed). Uma classe só em vez de 4
/// quase-idênticas, cada instância com sua própria fila e seu próprio delegate apontando pro
/// método da etapa certa. Reaplica sessão + traceparent capturados na publicação — sem isso a
/// árvore de spans no Sentry e as propriedades do Helicone se perderiam entre uma etapa e a
/// próxima.
/// </summary>
public class EtapaQueueConsumer(
    IFilaConsumer<VideoEtapaMensagem> filaConsumer,
    Func<Domain.Entities.Video, CancellationToken, Task> processarEtapa,
    string nomeSpan,
    ITracingService tracingService,
    ISessionContext sessionContext,
    ILogger<EtapaQueueConsumer> logger)
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

            using var trace = tracingService.IniciarTrace(nomeSpan, "video_pipeline", mensagem.TraceParent);

            logger.LogInformation("Consumindo {Span} — {Id} ({Title}, canal {Canal})", nomeSpan, video.Id, video.Title, video.ChannelTitle);

            try
            {
                await processarEtapa(video, token);
            }
            catch (Exception ex)
            {
                trace.MarcarErro(ex);
                logger.LogError(ex, "Erro na etapa {Span} do vídeo {Id} — {Title}", nomeSpan, video.Id, video.Title);
                throw; // relança — o IFilaConsumer decide a retentativa (nack/requeue), não confirma a mensagem
            }
        }, ct);
}
