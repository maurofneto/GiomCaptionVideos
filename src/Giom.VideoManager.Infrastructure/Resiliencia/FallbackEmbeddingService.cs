using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Encadeia N providers de IEmbeddingService (Feature #101) — hoje só "OpenAI" na cadeia
/// padrão, mas o mecanismo já suporta acrescentar outros (ex: VoyageAI) mais tarde, já que a
/// origem de cada embedding é filtrada no Supabase (ver VideoChunk.Origem) e não corrompe mais a
/// comparação de vetores entre provedores diferentes.
/// </summary>
public sealed class FallbackEmbeddingService(
    IReadOnlyList<(string Nome, IEmbeddingService Servico)> cadeia,
    ILogger<FallbackEmbeddingService> logger) : IEmbeddingService
{
    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
        var elos = cadeia
            .Select(c => (c.Nome, (Func<CancellationToken, Task<bool>>)(async token =>
            {
                await c.Servico.IndexarAsync(video, token);
                return true;
            })))
            .ToList();

        await FallbackChain.Montar(elos).ExecutarAsync("embedding_indexar", logger, ct);
    }

    public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
    {
        var elos = cadeia
            .Select(c => (c.Nome, (Func<CancellationToken, Task<BuscaSemanticaResultado>>)(token => c.Servico.BuscarAsync(pergunta, area, canalId, limite, threshold, token))))
            .ToList();

        return FallbackChain.Montar(elos).ExecutarAsync("embedding_buscar", logger, ct);
    }
}
