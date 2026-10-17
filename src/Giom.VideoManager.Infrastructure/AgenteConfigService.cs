using System.Collections.Concurrent;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure;

/// <summary>
/// Cache curto (TTL) sobre <see cref="IAgenteConfigRepository"/>: o bot lê a instrução de concisão a
/// cada resposta, mas só vai ao banco quando o valor expira, então mudar agente_config pega em ~1 min
/// sem deploy. Falha do banco nunca derruba o bot — devolve o último valor conhecido (ou vazio).
/// </summary>
public class AgenteConfigService(
    IAgenteConfigRepository repository,
    ILogger<AgenteConfigService> logger,
    TimeProvider? timeProvider = null) : IAgenteConfigService
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _tempo = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (string Valor, DateTimeOffset Expira)> _cache = new();

    public async Task<string> ObterInstrucaoConcisaoAsync(string provedor, CancellationToken ct = default)
    {
        var agora = _tempo.GetUtcNow();
        if (_cache.TryGetValue(provedor, out var cacheado) && cacheado.Expira > agora)
            return cacheado.Valor;

        try
        {
            var valor = await repository.ObterInstrucaoConcisaoAsync(provedor, ct);
            _cache[provedor] = (valor, agora + Ttl);
            return valor;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falha ao ler agente_config de {Provedor}; usando o último valor conhecido", provedor);
            return cacheado.Valor ?? string.Empty;
        }
    }
}
