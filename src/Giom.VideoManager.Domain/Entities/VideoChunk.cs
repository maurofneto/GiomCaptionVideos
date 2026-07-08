namespace Giom.VideoManager.Domain.Entities;

public class VideoChunk
{
    public string VideoId { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public string Conteudo { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = [];

    /// <summary>
    /// Provider que gerou este embedding (ex: "OpenAI", "VoyageAI") — usado pra filtrar a busca
    /// semântica (Feature #101): vetores de provedores diferentes não são comparáveis por
    /// similaridade de cosseno entre si, então o Supabase só compara chunks da MESMA origem que
    /// gerou o embedding da pergunta.
    /// </summary>
    public string Origem { get; set; } = string.Empty;
}
