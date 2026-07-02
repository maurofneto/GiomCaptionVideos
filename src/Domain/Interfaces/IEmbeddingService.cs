using Domain.Entities;

namespace Domain.Interfaces;

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
    Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, int limite = 6, double threshold = 0.70, CancellationToken ct = default);
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
