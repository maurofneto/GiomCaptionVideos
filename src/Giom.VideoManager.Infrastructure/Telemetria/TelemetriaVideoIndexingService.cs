using System.Diagnostics;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>
/// Decorator genérico de IVideoIndexingService (Feature #115) — só indexação; busca semântica
/// tem seu próprio decorator (TelemetriaBuscaSemanticaService), já que as duas viraram
/// interfaces separadas (ISP).
/// Custo (CustoEstimadoUsd) só aparece com a implementação Direct (chama a OpenAI e reporta via
/// IContextoTelemetria) — a implementação via Edge Function do Supabase não expõe o custo do
/// lado do .NET, porque a chamada à OpenAI acontece dentro da própria function.
/// Registrado via Scrutor (services.Decorate&lt;IVideoIndexingService, TelemetriaVideoIndexingService&gt;()
/// na Composition) — Scrutor resolve "inner" como a implementação JÁ registrada antes do
/// Decorate, sem precisar de generics nem de "new" montando a decoração à mão.
/// </summary>
public class TelemetriaVideoIndexingService(IVideoIndexingService inner, ITelemetriaService telemetria, ContextoTelemetria contextoTelemetria, ITracingService tracingService, ILogger<TelemetriaVideoIndexingService> logger) : IVideoIndexingService
{
    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
        using var span = tracingService.IniciarEtapaAtual("embedding");
        var stopwatch = Stopwatch.StartNew();
        var meta = contextoTelemetria.IniciarEscopo();
        try
        {
            await inner.IndexarAsync(video, ct);
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "embedding",
                EntidadeId: video.Id,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: true,
                Provedor: meta.Provedor,
                Modelo: meta.Modelo,
                TokensEntrada: meta.TokensEntrada,
                CustoEstimadoUsd: meta.CustoEstimadoUsd
            ), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao indexar embedding para {VideoId}", video.Id);
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "embedding",
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
