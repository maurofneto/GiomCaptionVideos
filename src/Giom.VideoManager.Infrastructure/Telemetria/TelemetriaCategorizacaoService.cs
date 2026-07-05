using System.Diagnostics;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>Decorator genérico — ver TelemetriaResumoService para a explicação do padrão.</summary>
public class TelemetriaCategorizacaoService(ICategorizacaoService inner, ITelemetriaService telemetria, ContextoTelemetria contextoTelemetria, ILogger<TelemetriaCategorizacaoService> logger) : ICategorizacaoService
{
    public async Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var resultado = await inner.ClassificarAsync(video, ct);
            var meta = contextoTelemetria.Consumir();
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "categorizacao",
                EntidadeId: video.Id,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: true,
                Provedor: meta?.Provedor,
                Modelo: meta?.Modelo,
                TokensEntrada: meta?.TokensEntrada,
                TokensSaida: meta?.TokensSaida,
                Detalhes: meta?.Detalhes
            ), ct);
            return resultado;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao classificar vídeo {VideoId}", video.Id);
            var meta = contextoTelemetria.Consumir();
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "categorizacao",
                EntidadeId: video.Id,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: false,
                Provedor: meta?.Provedor,
                Modelo: meta?.Modelo
            ), ct);
            throw;
        }
    }
}
