using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Variante de IEmbeddingService pra indexação em pipeline (Feature #115) — troca de
/// FallbackEmbeddingService é só uma decisão de Composition (Embedding:Modo), o chamador
/// (EmbeddingEtapaHandler) injeta IEmbeddingService normalmente e nunca sabe qual das duas está
/// por trás. IndexarAsync roda TODOS os provedores da cadeia (uma falha não impede os outros —
/// cada um grava seus próprios chunks, Origem diferente). BuscarAsync mantém a MESMA semântica
/// de fallback de FallbackEmbeddingService (busca sempre para no primeiro que funcionar,
/// independente do modo de indexação escolhido).
/// </summary>
public sealed class PipelineEmbeddingService(
    IReadOnlyList<(string Nome, IEmbeddingService Servico)> cadeia,
    ILogger<PipelineEmbeddingService> logger) : IEmbeddingService
{
    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
        foreach (var (nome, servico) in cadeia)
        {
            try
            {
                await servico.IndexarAsync(video, ct);
            }
            catch (Exception ex)
            {
                // Log (não throw) é deliberado — segue pros próximos provedores mesmo com falha
                // de um deles (diferente de FallbackHandler, que existe pra PARAR no primeiro
                // sucesso).
                logger.LogError(ex, "Pipeline de indexação: provedor {Provider} falhou pra {VideoId} — seguindo com os demais", nome, video.Id);
            }
        }
    }

    public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
    {
        var elos = cadeia
            .Select(c => (c.Nome, (Func<CancellationToken, Task<BuscaSemanticaResultado>>)(token => c.Servico.BuscarAsync(pergunta, area, canalId, limite, threshold, token))))
            .ToList();

        return FallbackChain.Montar(elos).ExecutarAsync("embedding_buscar", logger, ct);
    }
}
