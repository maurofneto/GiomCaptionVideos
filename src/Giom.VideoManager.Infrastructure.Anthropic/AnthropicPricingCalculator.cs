namespace Giom.VideoManager.Infrastructure.Anthropic;

/// <summary>
/// Calculadora de custo estimado por modelo da Anthropic — conhecimento específico de
/// provedor fica isolado aqui (Infrastructure.Anthropic); o decorator genérico de telemetria
/// e o Domain nunca sabem de preço por token de nenhum provedor específico, só repassam o
/// valor (nullable) que a implementação concreta calculou.
/// Preços em USD por 1M tokens — conferir https://www.anthropic.com/pricing antes de confiar
/// cegamente, tende a mudar, e alguns modelos têm preço promocional com prazo de validade.
/// </summary>
public static class AnthropicPricingCalculator
{
    private sealed record Preco(decimal EntradaPorMilhao, decimal SaidaPorMilhao);

    // Sonnet 5 em preço promocional até 2026-08-31 (US$2,00/US$10,00 por 1M) — revisar depois dessa data.
    private static readonly Dictionary<string, Preco> Precos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-sonnet-5"] = new Preco(2.00m, 10.00m),
        ["claude-haiku-4-5-20251001"] = new Preco(1.00m, 5.00m),
        ["claude-haiku-4-5"] = new Preco(1.00m, 5.00m),
    };

    /// <summary>Retorna null se o modelo não estiver na tabela — melhor não estimar do que estimar errado.</summary>
    public static decimal? Calcular(string modelo, int? tokensEntrada, int? tokensSaida)
    {
        if (string.IsNullOrWhiteSpace(modelo) || !Precos.TryGetValue(modelo, out var preco))
            return null;

        var custoEntrada = (tokensEntrada ?? 0) * preco.EntradaPorMilhao / 1_000_000m;
        var custoSaida   = (tokensSaida ?? 0) * preco.SaidaPorMilhao / 1_000_000m;
        return custoEntrada + custoSaida;
    }
}
