using Domain.Entities;

namespace Domain.Interfaces;

/// <summary>
/// Gera um resumo a partir da transcrição de um vídeo.
/// Implementações: ClaudeResumoService, SummarizeTechResumoService
/// </summary>
public interface IResumoService
{
    Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default);
}
