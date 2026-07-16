using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Encadeia N providers de IEmbeddingService (Feature #101) pra busca semântica — tenta em
/// ordem, para no primeiro que funcionar. Único implementador de IBuscaSemanticaService (ver
/// Feature #115) — indexação usa PipelineEmbeddingService (roda todos), interfaces separadas
/// (ISP) pra ChatBotProcessor nunca precisar saber que "indexar" existe.
/// </summary>
public sealed class FallbackEmbeddingService(
    IReadOnlyList<(string Nome, IEmbeddingService Servico)> cadeia,
    ILogger<FallbackEmbeddingService> logger) : IBuscaSemanticaService
{
    public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
    {
        var elos = cadeia
            .Select(c => (c.Nome, (Func<CancellationToken, Task<BuscaSemanticaResultado>>)(token => c.Servico.BuscarAsync(pergunta, area, canalId, limite, threshold, token))))
            .ToList();

        return FallbackChain.Montar(elos).ExecutarAsync("embedding_buscar", logger, ct);
    }
}
