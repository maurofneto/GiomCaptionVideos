namespace Giom.VideoManager.Domain.Entities;

public class Capitulo
{
    public int Id { get; set; }
    public int ResumoId { get; set; }
    public TimeSpan Inicio { get; set; }
    public string Texto { get; set; } = string.Empty;
}
