using System.Diagnostics;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>
/// Decorator genérico: só instrumenta IndexarAsync (a etapa da pipeline de vídeo) — BuscarAsync
/// passa direto pro inner sem telemetria própria, porque o ChatBotProcessor já registra o
/// round-trip de busca RAG do ponto de vista do bot (tipo "busca_rag"); duplicar aqui só
/// geraria uma segunda medição da mesma chamada.
/// Custo (CustoEstimadoUsd) só aparece com a implementação Direct (chama a OpenAI e reporta via
/// IContextoTelemetria) — a implementação via Edge Function do Supabase não expõe o custo do
/// lado do .NET, porque a chamada à OpenAI acontece dentro da própria function.
/// Registrado via Scrutor (services.Decorate&lt;IEmbeddingService, TelemetriaEmbeddingService&gt;()
/// na Composition) — Scrutor resolve "inner" como a implementação JÁ registrada antes do
/// Decorate, sem precisar de generics nem de "new" montando a decoração à mão.
/// </summary>
public class TelemetriaEmbeddingService(IEmbeddingService inner, ITelemetriaService telemetria, ContextoTelemetria contextoTelemetria, ILogger<TelemetriaEmbeddingService> logger) : IEmbeddingService
{
    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
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

    public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
        => inner.BuscarAsync(pergunta, area, canalId, limite, threshold, ct);
}
