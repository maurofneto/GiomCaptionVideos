using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>Encadeia N providers de IAgentService (ex: Claude → Grok → OpenAI) via FallbackChain.</summary>
public sealed class FallbackAgentService(
    IReadOnlyList<(string Nome, IAgentService Servico)> cadeia,
    ILogger<FallbackAgentService> logger) : IAgentService
{
    public Task<string> ResponderAsync(string chatId, TurnoConversa turnoAtual, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default)
    {
        var elos = cadeia
            .Select(c => (c.Nome, (Func<CancellationToken, Task<string>>)(token => c.Servico.ResponderAsync(chatId, turnoAtual, contexto, historico, token))))
            .ToList();

        return FallbackChain.Montar(elos).ExecutarAsync("agent", logger, ct);
    }
}
