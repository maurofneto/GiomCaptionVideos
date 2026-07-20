namespace Giom.VideoManager.Infrastructure.Precos;

/// <summary>
/// Preço por 1M tokens de todo modelo usado no pipeline — antes cada provedor
/// (AnthropicPricingCalculator, OpenAiPricingCalculator, VoyagePricingCalculator,
/// CoherePricingCalculator) reimplementava a MESMA lógica (achar o modelo na tabela, dividir por
/// 1M, retornar null se não achar); só a tabela de preços mudava, e Grok tinha ficado sem
/// nenhuma (nunca foi criada). Unificado aqui, no kernel Infrastructure — todo provedor de IA já
/// referencia este projeto (mesmo padrão de TextChunker, ver Infrastructure.Resiliencia).
///
/// Chave é o nome de "provedor" exatamente como passado em IContextoTelemetria.Registrar (ex:
/// "Claude", não "Anthropic" — a Anthropic API vira "Claude" em todo o resto do código), pra
/// nunca precisar de tradução entre os dois.
/// </summary>
public static class PricingCalculator
{
    private sealed record Preco(decimal EntradaPorMilhao, decimal SaidaPorMilhao);

    // Preços em USD por 1M tokens — conferir a página oficial de cada provedor antes de confiar
    // cegamente, tende a mudar (Sonnet 5 tem preço promocional com prazo, por exemplo).
    // Embeddings (OpenAI/VoyageAI/Cohere) só têm 1 tipo de token: SaidaPorMilhao fica 0 e quem
    // chama passa tokensSaida: null (ou omite).
    private static readonly Dictionary<(string Provedor, string Modelo), Preco> Precos =
        new(ProvedorModeloComparer.Instance)
        {
            // Claude (chat) — Sonnet 5 em preço promocional até 2026-08-31 (US$2,00/US$10,00 por 1M).
            [("Claude", "claude-sonnet-5")] = new(2.00m, 10.00m),
            [("Claude", "claude-haiku-4-5-20251001")] = new(1.00m, 5.00m),
            [("Claude", "claude-haiku-4-5")] = new(1.00m, 5.00m),

            // OpenAI (embeddings)
            [("OpenAI", "text-embedding-3-small")] = new(0.02m, 0m),
            [("OpenAI", "text-embedding-3-large")] = new(0.13m, 0m),

            // OpenAI (chat, Resumo/Categorização) — nunca teve entrada aqui (só embeddings);
            // conferir https://openai.com/api/pricing antes de confiar cegamente.
            [("OpenAI", "gpt-4o-mini")] = new(0.15m, 0.60m),
            [("OpenAI", "gpt-4o")] = new(2.50m, 10.00m),

            // VoyageAI (embeddings) — cota gratuita de 200M tokens na criação da conta NÃO
            // descontada aqui, é o custo de tabela cheio (ver Bug #118/#122).
            [("VoyageAI", "voyage-3-lite")] = new(0.02m, 0m),
            [("VoyageAI", "voyage-3")] = new(0.06m, 0m),
            [("VoyageAI", "voyage-3-large")] = new(0.18m, 0m),
            [("VoyageAI", "voyage-code-3")] = new(0.18m, 0m),

            // Cohere (embeddings)
            [("Cohere", "embed-v4.0")] = new(0.12m, 0m),
            [("Cohere", "embed-english-v3.0")] = new(0.10m, 0m),
            [("Cohere", "embed-multilingual-v3.0")] = new(0.10m, 0m),

            // Grok/xAI (chat) — nunca teve calculadora própria; conferir https://x.ai/api antes de confiar cegamente.
            [("Grok", "grok-3-mini")] = new(0.30m, 0.50m),
            [("Grok", "grok-3")] = new(3.00m, 15.00m),

            // Kimi/Moonshot (chat, US #124) — US$3,00 entrada (cache miss — cache hit é
            // US$0,30, não modelado aqui, custo de tabela cheio) / US$15,00 saída por 1M
            // tokens; conferir platform.kimi.ai/docs/pricing/chat-k3 antes de confiar cegamente.
            [("Kimi", "kimi-k3")] = new(3.00m, 15.00m),
        };

    /// <summary>Retorna null se provedor+modelo não estiver na tabela — melhor não estimar do que estimar errado.</summary>
    public static decimal? Calcular(string provedor, string? modelo, int? tokensEntrada, int? tokensSaida = null)
    {
        if (string.IsNullOrWhiteSpace(provedor) || string.IsNullOrWhiteSpace(modelo))
            return null;
        if (!Precos.TryGetValue((provedor, modelo), out var preco))
            return null;

        var custoEntrada = (tokensEntrada ?? 0) * preco.EntradaPorMilhao / 1_000_000m;
        var custoSaida = (tokensSaida ?? 0) * preco.SaidaPorMilhao / 1_000_000m;
        return custoEntrada + custoSaida;
    }

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
