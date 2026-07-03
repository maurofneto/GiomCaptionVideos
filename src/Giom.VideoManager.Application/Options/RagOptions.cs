namespace Giom.VideoManager.Application.Options;

/// <summary>
/// Parâmetros da busca semântica (RAG) usados pelo ChatBotProcessor. Configurável via
/// appsettings/SSM em vez de depender do valor default de IEmbeddingService.BuscarAsync —
/// já tivemos um bug real de threshold desalinhado (0.70 vs 0.35) por causa disso.
/// </summary>
public class RagOptions
{
    public int Limite { get; init; } = 6;
    public double Threshold { get; init; } = 0.35;
}
