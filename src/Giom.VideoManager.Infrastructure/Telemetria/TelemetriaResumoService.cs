using System.Diagnostics;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>
/// Decorator genérico: qualquer IResumoService (Claude, ou outro provedor no futuro) passa a
/// registrar telemetria automaticamente, sem precisar conhecer ITelemetriaService. Metadados
/// ricos (provedor/modelo/tokens) só aparecem se a implementação decorada chamar
/// IContextoTelemetria.Registrar — sem isso, tempo/sucesso/falha ainda são registrados.
/// </summary>
public class TelemetriaResumoService(IResumoService inner, ITelemetriaService telemetria, ContextoTelemetria contextoTelemetria, ILogger<TelemetriaResumoService> logger) : IResumoService
{
    public async Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var meta = contextoTelemetria.IniciarEscopo();
        try
        {
            var resultado = await inner.GerarResumoAsync(video, ct);
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "resumo",
                EntidadeId: video.Id,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: true,
                Provedor: meta.Provedor,
                Modelo: meta.Modelo,
                TokensEntrada: meta.TokensEntrada,
                TokensSaida: meta.TokensSaida,
                Detalhes: meta.Detalhes
            ), ct);
            return resultado;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao gerar resumo para {VideoId}", video.Id);
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "resumo",
                EntidadeId: video.Id,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: false,
                Provedor: meta.Provedor,
                Modelo: meta.Modelo
            ), ct);
            throw;
        }
    }
}
