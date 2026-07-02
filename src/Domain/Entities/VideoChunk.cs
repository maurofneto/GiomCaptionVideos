namespace Domain.Entities;

public class VideoChunk
{
    public string VideoId { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public string Conteudo { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = [];
}
