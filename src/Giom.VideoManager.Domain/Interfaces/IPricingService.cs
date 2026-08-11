namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Calcula o custo de uma chamada de IA a partir do preço por 1M tokens (provedor_preco, Task
/// #130) — substitui o antigo PricingCalculator estático. Mesma assinatura/semântica do método
/// que substitui (retorna null se provedor+modelo não estiver cadastrado, nunca lança).
/// </summary>
public interface IPricingService
{
    Task<decimal?> CalcularAsync(string provedor, string? modelo, int? tokensEntrada, int? tokensSaida = null, CancellationToken ct = default);
}
