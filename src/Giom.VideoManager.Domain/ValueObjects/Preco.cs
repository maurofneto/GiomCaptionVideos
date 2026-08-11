namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Preço por 1M tokens de um (Provedor, Modelo) — Task #130. Embeddings só têm 1 tipo de token:
/// SaidaPorMilhao fica 0 e quem calcula passa tokensSaida: null (ver IPricingService).
/// </summary>
public record Preco(
    string Provedor,
    string Modelo,
    decimal EntradaPorMilhao,
    decimal SaidaPorMilhao);
