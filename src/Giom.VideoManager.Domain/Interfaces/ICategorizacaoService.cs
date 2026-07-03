using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Classifica um vídeo nas áreas da Teologia Sistemática com pesos.
/// Retorna múltiplas categorias — a de maior peso é a principal.
/// Implementações: ClaudeCategorizacaoService
/// </summary>
public interface ICategorizacaoService
{
    Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, CancellationToken ct = default);
}
