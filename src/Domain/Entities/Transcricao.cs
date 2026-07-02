namespace Domain.Entities;

public class Transcricao
{
    public int Id { get; set; }
    public string VideoId { get; set; } = string.Empty;
    /// <summary>Conteúdo original preservado com timestamps e tags do formato fonte.</summary>
    public string Conteudo { get; set; } = string.Empty;

    /// <summary>Conteúdo com timestamps e tags removidos — pronto para IA e embeddings.</summary>
    public string ConteudoLimpo { get; set; } = string.Empty;

    /// <summary>Formato do arquivo: sbv, srt, vtt, txt</summary>
    public string Formato { get; set; } = string.Empty;

    /// <summary>Origem: YouTubeCaption, Whisper</summary>
    public string Origem { get; set; } = string.Empty;
    public DateTime GeradoEm { get; set; }
}
