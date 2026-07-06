namespace Giom.VideoManager.Infrastructure.OpenAI;

/// <summary>
/// Calculadora de custo estimado por modelo de embedding da OpenAI — conhecimento específico de
/// provedor fica isolado aqui (Infrastructure.OpenAI); o decorator genérico de telemetria e o
/// Domain nunca sabem de preço por token de nenhum provedor específico, só repassam o valor
/// (nullable) que a implementação concreta calculou.
/// Preços em USD por 1M tokens — conferir https://openai.com/api/pricing antes de confiar
/// cegamente, tende a mudar. Embeddings só têm um tipo de token (sem separar entrada/saída).
/// </summary>
public static class OpenAiPricingCalculator
{
    private static readonly Dictionary<string, decimal> PrecoPorMilhao = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text-embedding-3-small"] = 0.02m,
        ["text-embedding-3-large"] = 0.13m,
    };

    /// <summary>Retorna null se o modelo não estiver na tabela — melhor não estimar do que estimar errado.</summary>
    public static decimal? Calcular(string modelo, int? tokens)
    {
        if (string.IsNullOrWhiteSpace(modelo) || tokens is null || !PrecoPorMilhao.TryGetValue(modelo, out var preco))
            return null;

        return tokens.Value * preco / 1_000_000m;
    }
}
