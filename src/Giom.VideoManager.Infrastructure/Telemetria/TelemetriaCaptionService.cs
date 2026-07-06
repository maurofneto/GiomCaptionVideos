using System.Diagnostics;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>
/// Decorator genérico: qualquer ICaptionService (YouTube ou outra fonte no futuro) passa a
/// registrar telemetria automaticamente, sem precisar conhecer ITelemetriaService. Nenhum dos
/// providers atuais cobra por chamada (legenda direto da plataforma, sem custo de IA), então
/// CustoEstimadoUsd fica sempre null aqui — só tempo/sucesso são registrados.
/// </summary>
public class TelemetriaCaptionService(ICaptionService inner, ITelemetriaService telemetria, ILogger<TelemetriaCaptionService> logger) : ICaptionService
{
    public async Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var resultado = await inner.GetCaptionAsync(video, ct);
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "transcricao",
                EntidadeId: video.Id,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: true
            ), ct);
            return resultado;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao obter legenda para {VideoId}", video.Id);
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "transcricao",
                EntidadeId: video.Id,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: false
            ), ct);
            throw;
        }
    }
}
