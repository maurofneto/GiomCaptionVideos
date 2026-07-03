namespace Giom.VideoManager.Domain.Entities;

public class Resumo
{
    public int Id { get; set; }
    public string VideoId { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;

    /// <summary>Origem: Claude, SummarizeTech</summary>
    public string Origem { get; set; } = string.Empty;
    public DateTime GeradoEm { get; set; }
    public Capitulo[] Capitulos { get; set; } = [];
}
