using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>Encadeia N providers de ICategorizacaoService (ex: Claude → Grok → OpenAI) via FallbackChain.</summary>
public sealed class FallbackCategorizacaoService(
    IReadOnlyList<(string Nome, ICategorizacaoService Servico)> cadeia,
    ILogger<FallbackCategorizacaoService> logger) : ICategorizacaoService
{
    public Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, CancellationToken ct = default)
    {
        var elos = cadeia
            .Select(c => (c.Nome, (Func<CancellationToken, Task<IReadOnlyList<CategoriaTeologica>>>)(token => c.Servico.ClassificarAsync(video, token))))
            .ToList();

        return FallbackChain.Montar(elos).ExecutarAsync("categorizacao", logger, ct);
    }
}
