using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>
/// Distribui cada registro de telemetria pra todos os sinks configurados (Supabase, Sentry, o
/// que vier depois) — é o único ITelemetriaService de fato injetado no resto do app (decorators,
/// ChatBotProcessor). Falha num sink não derruba os outros nem quem chamou RegistrarAsync —
/// telemetria nunca deveria quebrar a operação de negócio que a originou.
/// </summary>
public class CompositeTelemetriaService(IReadOnlyList<ITelemetriaService> sinks, ILogger<CompositeTelemetriaService> logger) : ITelemetriaService
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
