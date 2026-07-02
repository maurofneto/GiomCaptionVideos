namespace Domain.Entities;

public class Transcricao
{
    public int Id { get; set; }
    public string VideoId { get; set; } = string.Empty;
    public string Conteudo { get; set; } = string.Empty;

    /// <summary>Formato do arquivo: sbv, srt, vtt, txt</summary>
    public string Formato { get; set; } = string.Empty;

    /// <summary>Origem: YouTubeCaption, Whisper</summary>
    public string Origem { get; set; } = string.Empty;
    public DateTime GeradoEm { get; set; }
}
