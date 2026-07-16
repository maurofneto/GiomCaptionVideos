using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Domain.Interfaces;

public interface IVideoChunkRepository
{
    Task DeletarPorVideoAsync(string videoId, CancellationToken ct = default);
    Task InserirAsync(IEnumerable<VideoChunk> chunks, CancellationToken ct = default);

    /// <summary>
    /// Compara um embedding já calculado pelo chamador contra o corpus indexado (RRF híbrido,
    /// diversidade por aula, filtro por Origem — Feature #101). Os providers de embedding
    /// client-side (OpenAI/Voyage/Cohere) já dependem deste repositório pra persistir chunks —
    /// reaproveitar a mesma porta pra buscar evita uma abstração nova ao lado (o agregado é o
    /// mesmo, VideoChunk) e mantém esses projetos sem conhecer Supabase/Postgres diretamente.
    /// </summary>
    Task<BuscaSemanticaResultado> BuscarSimilaresAsync(
        string pergunta,
        float[] embedding,
        string origem,
        string? area = null,
        string? canalId = null,
        int limite = 6,
        double threshold = 0.35,
        CancellationToken ct = default);
}
