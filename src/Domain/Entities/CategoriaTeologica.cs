using Domain.Enums;

namespace Domain.Entities;

public class CategoriaTeologica
{
    public int Id { get; set; }
    public string VideoId { get; set; } = string.Empty;
    public AreaTeologica Area { get; set; }
    public string Subcategoria { get; set; } = string.Empty;

    /// <summary>
    /// Relevância desta área para o vídeo (0.0 a 1.0).
    /// A categoria com maior peso é a principal.
    /// </summary>
    public decimal Peso { get; set; } = 1.0m;

    public DateTime ClassificadoEm { get; set; }
}
