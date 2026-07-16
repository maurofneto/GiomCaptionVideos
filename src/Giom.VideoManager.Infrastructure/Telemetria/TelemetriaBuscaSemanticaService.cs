using System.Diagnostics;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>
/// Decorator genérico de IBuscaSemanticaService — espelha TelemetriaVideoIndexingService (indexação),
/// mas pro lado de busca. Antes desta classe existir, a busca RAG do bot só tinha o registro
/// "busca_rag" do próprio ChatBotProcessor (tempo/sucesso/quantidade de chunks), sem provedor,
/// modelo, tokens nem custo — informação que os providers já calculam mas ninguém registrava
/// (Feature #115, separação IVideoIndexingService/IBuscaSemanticaService deixou essa lacuna
/// visível). Tipo "embedding_busca" pra não colidir com "busca_rag" (que é o round-trip do bot,
/// não a chamada ao provedor de embedding em si).
/// Registrado via Scrutor (services.Decorate&lt;IBuscaSemanticaService, TelemetriaBuscaSemanticaService&gt;()
/// na Composition).
/// </summary>
public class TelemetriaBuscaSemanticaService(IBuscaSemanticaService inner, ITelemetriaService telemetria, ContextoTelemetria contextoTelemetria, ITracingService tracingService, ILogger<TelemetriaBuscaSemanticaService> logger) : IBuscaSemanticaService
{
    public async Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
    {
        using var span = tracingService.IniciarEtapaAtual("embedding_busca");
        var stopwatch = Stopwatch.StartNew();
        var meta = contextoTelemetria.IniciarEscopo();
        try
        {
            var resultado = await inner.BuscarAsync(pergunta, area, canalId, limite, threshold, ct);
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "embedding_busca",
                EntidadeId: canalId ?? "sem-canal",
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: true,
                Provedor: meta.Provedor,
                Modelo: meta.Modelo,
                TokensEntrada: meta.TokensEntrada,
                CustoEstimadoUsd: meta.CustoEstimadoUsd
            ), ct);
            return resultado;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro na busca semântica ({CanalId})", canalId);
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "embedding_busca",
                EntidadeId: canalId ?? "sem-canal",
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: false,
                Provedor: meta.Provedor,
                Modelo: meta.Modelo
            ), ct);
            throw;
        }
    }
}
