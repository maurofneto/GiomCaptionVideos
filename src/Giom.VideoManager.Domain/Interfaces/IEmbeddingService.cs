using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Indexa transcrições em chunks com embeddings para RAG.
/// Implementações: SupabaseEmbeddingService
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Dispara a indexação do vídeo na Edge Function.
    /// A função busca título e transcrição diretamente do banco — sem enviar dados no body.
    /// </summary>
    Task IndexarAsync(Video video, CancellationToken ct = default);

    /// <summary>
    /// Busca os chunks mais semanticamente próximos da pergunta.
    /// Retorna chunks de múltiplos vídeos, mais o contexto já formatado para o Claude.
    /// </summary>
    /// <param name="canalId">Restringe a busca a um único canal do YouTube (ex.: para o bot do Instituto nunca retornar conteúdo do Ministério); null = sem restrição.</param>
    Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default);
}

/// <summary>Resultado da busca semântica por chunks.</summary>
public record BuscaSemanticaResultado(
    IReadOnlyList<ChunkSimilar> Chunks,
    string ContextoFormatado
);

/// <summary>Chunk individual retornado pela busca.</summary>
public record ChunkSimilar(
    string VideoId,
    string Titulo,
    int ChunkIndex,
    string Conteudo,
    string? Area,
    string? Subcategoria,
    double Similarity
);
