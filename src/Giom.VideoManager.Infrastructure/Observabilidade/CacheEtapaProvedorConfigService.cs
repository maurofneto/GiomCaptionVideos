using System.Collections.Concurrent;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Infrastructure.Observabilidade;

/// <summary>
/// Decorator de cache em memória (US #106) — ConcurrentDictionary por processo, sem TTL: os
/// workers que consomem isso (Resumo/Categorizacao.Worker) drenam a fila e saem (mesmo padrão
/// validado no #105), então cache-por-processo já basta. Pra cenário de worker de vida longa
/// (Service com auto scaling por fila, ainda não implementado), ver RedisCacheEtapaProvedorConfigService
/// (Infrastructure.Redis) — mesma interface, TTL configurável, compartilhado entre instâncias.
/// </summary>
public sealed class CacheEtapaProvedorConfigService(IEtapaProvedorConfigService inner) : IEtapaProvedorConfigService
{
    private readonly ConcurrentDictionary<(string ChannelId, string Etapa), IReadOnlyList<EtapaProvedorConfig>> _cache = new();

    public async Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default)
    {
        if (_cache.TryGetValue((channelId, etapa), out var cadeia))
            return cadeia;

        cadeia = await inner.ObterCadeiaAsync(channelId, etapa, ct);
        _cache[(channelId, etapa)] = cadeia;
        return cadeia;
    }
}
