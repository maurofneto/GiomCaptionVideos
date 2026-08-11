using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Precos;

/// <summary>
/// Substitui o antigo PricingCalculator estático (Task #130) — mesma lógica de cálculo, preço
/// agora vem de IPricingRepository (tabela provedor_preco) em vez de dicionário em código.
/// </summary>
public sealed class PricingService(IPricingRepository repository) : IPricingService
{
    /// <summary>Retorna null se provedor/modelo vazio ou não cadastrado — melhor não estimar do que estimar errado.</summary>
    public async Task<decimal?> CalcularAsync(string provedor, string? modelo, int? tokensEntrada, int? tokensSaida = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provedor) || string.IsNullOrWhiteSpace(modelo))
            return null;

        var preco = await repository.ObterAsync(provedor, modelo, ct);
        if (preco is null)
            return null;

        var custoEntrada = (tokensEntrada ?? 0) * preco.EntradaPorMilhao / 1_000_000m;
        var custoSaida = (tokensSaida ?? 0) * preco.SaidaPorMilhao / 1_000_000m;
        return custoEntrada + custoSaida;
    }
}
