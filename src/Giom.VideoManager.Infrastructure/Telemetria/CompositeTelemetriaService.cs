using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>
/// Distribui cada registro de telemetria pra todos os sinks configurados — é o único
/// ITelemetriaService de fato injetado no resto do app (decorators, ChatBotProcessor). Recebe
/// os sinks via IEnumerable&lt;ITelemetriaSink&gt; (ver ITelemetriaSink) — a DI resolve
/// automaticamente todos os registrados, sem risco deste Composite se enumerar como seu
/// próprio sink e sem precisar montar a lista à mão. Adicionar um sink novo no futuro: só
/// implementar ITelemetriaSink e registrar — nada muda aqui.
/// Falha num sink não derruba os outros nem quem chamou RegistrarAsync — telemetria nunca
/// deveria quebrar a operação de negócio que a originou.
/// </summary>
public class CompositeTelemetriaService(IEnumerable<ITelemetriaSink> sinks, ILogger<CompositeTelemetriaService> logger) : ITelemetriaService
{
    public async Task RegistrarAsync(RegistroTelemetria registro, CancellationToken ct = default)
    {
        foreach (var sink in sinks)
        {
            try
            {
                await sink.RegistrarAsync(registro, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao registrar telemetria em {Sink}", sink.GetType().Name);
            }
        }
    }
}
