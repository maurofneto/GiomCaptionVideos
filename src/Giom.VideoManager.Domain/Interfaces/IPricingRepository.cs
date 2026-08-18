using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>Leitura da tabela provedor_preco (Task #130) — implementação HTTP em Infrastructure.Supabase.</summary>
public interface IPricingRepository
{
    /// <summary>Comparação de provedor/modelo é case-insensitive — mesmo comportamento do antigo PricingCalculator.</summary>
    Task<Preco?> ObterAsync(string provedor, string modelo, CancellationToken ct = default);
}
