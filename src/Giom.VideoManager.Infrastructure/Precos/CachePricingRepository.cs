using System.Collections.Concurrent;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Infrastructure.Precos;

/// <summary>
/// Decorator de cache em memória (Task #130) — mesmo padrão de CacheEtapaProvedorConfigService:
/// ConcurrentDictionary por processo, sem TTL. Preço muda raramente (não a cada vídeo, como a
/// config de prompt); o pior caso de um preço desatualizado é o mesmo de hoje (reiniciar o
/// worker já resolve).
/// </summary>
public sealed class CachePricingRepository(IPricingRepository inner) : IPricingRepository
{
    private readonly ConcurrentDictionary<(string Provedor, string Modelo), Preco?> _cache = new(ProvedorModeloComparer.Instance);

    public async Task<Preco?> ObterAsync(string provedor, string modelo, CancellationToken ct = default)
    {
        if (_cache.TryGetValue((provedor, modelo), out var preco))
            return preco;

        preco = await inner.ObterAsync(provedor, modelo, ct);
        _cache[(provedor, modelo)] = preco;
        return preco;
    }

    /// <summary>Mesmo comparer case-insensitive que o antigo PricingCalculator usava no dicionário estático.</summary>
    private sealed class ProvedorModeloComparer : IEqualityComparer<(string Provedor, string Modelo)>
    {
        public static readonly ProvedorModeloComparer Instance = new();

        public bool Equals((string Provedor, string Modelo) x, (string Provedor, string Modelo) y) =>
            string.Equals(x.Provedor, y.Provedor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Modelo, y.Modelo, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Provedor, string Modelo) obj) =>
            HashCode.Combine(obj.Provedor.ToUpperInvariant(), obj.Modelo.ToUpperInvariant());
    }
}
