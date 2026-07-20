using Giom.VideoManager.Infrastructure.Precos;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Precos;

/// <summary>
/// Substitui as antigas AnthropicPricingCalculatorTests/OpenAiPricingCalculatorTests (e a
/// ausência de testes pra Voyage/Cohere/Grok) — um calculador só, uma suíte só, cobrindo os
/// provedores que hoje têm preço na tabela.
/// </summary>
public class PricingCalculatorTests
{
    [Fact]
    public void Calcular_ProvedorOuModeloDesconhecido_RetornaNull()
    {
        Assert.Null(PricingCalculator.Calcular("Claude", "modelo-que-nao-existe", 1000, 1000));
        Assert.Null(PricingCalculator.Calcular("provedor-que-nao-existe", "claude-sonnet-5", 1000, 1000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Calcular_ModeloVazioOuNulo_RetornaNull(string? modelo)
    {
        Assert.Null(PricingCalculator.Calcular("Claude", modelo, 1000, 1000));
    }

    [Fact]
    public void Calcular_ClaudeSonnetComTokensConhecidos_CalculaCustoExato()
    {
        // Sonnet 5: US$2,00 entrada / US$10,00 saída por 1M tokens
        var custo = PricingCalculator.Calcular("Claude", "claude-sonnet-5", tokensEntrada: 1_000_000, tokensSaida: 1_000_000);

        Assert.Equal(12.00m, custo);
    }

    [Fact]
    public void Calcular_ClaudeHaikuComTokensConhecidos_CalculaCustoExato()
    {
        // Haiku 4.5: US$1,00 entrada / US$5,00 saída por 1M tokens
        var custo = PricingCalculator.Calcular("Claude", "claude-haiku-4-5-20251001", tokensEntrada: 500_000, tokensSaida: 200_000);

        Assert.Equal(0.50m + 1.00m, custo);
    }

    [Fact]
    public void Calcular_TokensNulos_TrataComoZero()
    {
        Assert.Equal(0m, PricingCalculator.Calcular("Claude", "claude-sonnet-5", tokensEntrada: null, tokensSaida: null));
    }

    [Fact]
    public void Calcular_SoTokensDeEntrada_IgnoraSaida()
    {
        var custo = PricingCalculator.Calcular("Claude", "claude-sonnet-5", tokensEntrada: 1_000_000, tokensSaida: null);

        Assert.Equal(2.00m, custo);
    }

    [Theory]
    [InlineData("OpenAI", "text-embedding-3-small", 1_000_000, 0.02)]
    [InlineData("OpenAI", "text-embedding-3-large", 500_000, 0.065)]
    [InlineData("VoyageAI", "voyage-3", 1_000_000, 0.06)]
    [InlineData("VoyageAI", "voyage-3-lite", 1_000_000, 0.02)]
    [InlineData("Cohere", "embed-v4.0", 1_000_000, 0.12)]
    public void Calcular_Embeddings_SoEntrada_CalculaCustoExato(string provedor, string modelo, int tokens, double custoEsperado)
    {
        var custo = PricingCalculator.Calcular(provedor, modelo, tokens);

        Assert.Equal((decimal)custoEsperado, custo);
    }

    [Fact]
    public void Calcular_Grok_TinhaFicadoSemCalculadora_AgoraTemPreco()
    {
        // Bug: Grok nunca teve PricingCalculator próprio — custoEstimadoUsd sempre saía null
        // nos registros de telemetria. Confirma que a tabela unificada cobre o provedor.
        var custo = PricingCalculator.Calcular("Grok", "grok-3-mini", tokensEntrada: 1_000_000, tokensSaida: 1_000_000);

        Assert.NotNull(custo);
        Assert.Equal(0.30m + 0.50m, custo);
    }

    [Fact]
    public void Calcular_OpenAiChat_TinhaFicadoSemCalculadora_AgoraTemPreco()
    {
        // Bug #131: OpenAiResumoService/OpenAiCategorizacaoService nunca chamavam
        // PricingCalculator (custoEstimadoUsd sempre saía null) — e o modelo de chat (gpt-4o-mini)
        // nem tinha entrada na tabela, só os embeddings da OpenAI. Confirma que os dois foram
        // corrigidos.
        var custo = PricingCalculator.Calcular("OpenAI", "gpt-4o-mini", tokensEntrada: 1_000_000, tokensSaida: 1_000_000);

        Assert.NotNull(custo);
        Assert.Equal(0.15m + 0.60m, custo);
    }

    [Fact]
    public void Calcular_ProvedorEModeloSaoCaseInsensitive()
    {
        var custoMinusculo = PricingCalculator.Calcular("claude", "claude-sonnet-5", 1000, 1000);
        var custoMaiusculo = PricingCalculator.Calcular("CLAUDE", "CLAUDE-SONNET-5", 1000, 1000);

        Assert.Equal(custoMinusculo, custoMaiusculo);
    }
}
