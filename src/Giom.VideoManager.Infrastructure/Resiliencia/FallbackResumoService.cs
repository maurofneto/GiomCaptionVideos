using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>Encadeia N providers de IResumoService (ex: Claude → Grok → OpenAI) via FallbackChain.</summary>
public sealed class FallbackResumoService(
    IReadOnlyList<(string Nome, IResumoService Servico)> cadeia,
    ILogger<FallbackResumoService> logger) : IResumoService
{
    public Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
    {
        var elos = cadeia
            .Select(c => (c.Nome, (Func<CancellationToken, Task<Resumo?>>)(token => c.Servico.GerarResumoAsync(video, token))))
            .ToList();

        return FallbackChain.Montar(elos).ExecutarAsync("resumo", logger, ct);
    }
}
