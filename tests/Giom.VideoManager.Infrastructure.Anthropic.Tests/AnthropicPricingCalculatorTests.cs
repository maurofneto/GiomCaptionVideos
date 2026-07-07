using Giom.VideoManager.Infrastructure.Anthropic;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Anthropic.Tests;

public class AnthropicPricingCalculatorTests
{
    [Fact]
    public void Calcular_ModeloDesconhecido_RetornaNull()
    {
        var custo = AnthropicPricingCalculator.Calcular("modelo-que-nao-existe", 1000, 1000);

        Assert.Null(custo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Calcular_ModeloVazioOuNulo_RetornaNull(string? modelo)
    {
        var custo = AnthropicPricingCalculator.Calcular(modelo!, 1000, 1000);

        Assert.Null(custo);
    }

    [Fact]
    public void Calcular_SonnetComTokensConhecidos_CalculaCustoExato()
    {
        // Sonnet 5: US$2,00 entrada / US$10,00 saída por 1M tokens
        var custo = AnthropicPricingCalculator.Calcular("claude-sonnet-5", tokensEntrada: 1_000_000, tokensSaida: 1_000_000);

        Assert.Equal(12.00m, custo);
    }

    [Fact]
    public void Calcular_HaikuComTokensConhecidos_CalculaCustoExato()
    {
        // Haiku 4.5: US$1,00 entrada / US$5,00 saída por 1M tokens
        var custo = AnthropicPricingCalculator.Calcular("claude-haiku-4-5-20251001", tokensEntrada: 500_000, tokensSaida: 200_000);

        Assert.Equal(0.50m + 1.00m, custo);
    }

    [Fact]
    public void Calcular_NomeDeModeloEhCaseInsensitive()
    {
        var custoMinusculo = AnthropicPricingCalculator.Calcular("claude-sonnet-5", 1000, 1000);
        var custoMaiusculo = AnthropicPricingCalculator.Calcular("CLAUDE-SONNET-5", 1000, 1000);

        Assert.Equal(custoMinusculo, custoMaiusculo);
    }

    [Fact]
    public void Calcular_TokensNulos_TrataComoZero()
    {
        var custo = AnthropicPricingCalculator.Calcular("claude-sonnet-5", tokensEntrada: null, tokensSaida: null);

        Assert.Equal(0m, custo);
    }

    [Fact]
    public void Calcular_SoTokensDeEntrada_IgnoraSaida()
    {
        var custo = AnthropicPricingCalculator.Calcular("claude-sonnet-5", tokensEntrada: 1_000_000, tokensSaida: null);

        Assert.Equal(2.00m, custo);
    }
}
