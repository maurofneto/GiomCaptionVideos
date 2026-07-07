using Giom.VideoManager.Infrastructure.OpenAI;
using Xunit;

namespace Giom.VideoManager.Infrastructure.OpenAI.Tests;

public class OpenAiPricingCalculatorTests
{
    [Fact]
    public void Calcular_ModeloDesconhecido_RetornaNull()
    {
        Assert.Null(OpenAiPricingCalculator.Calcular("modelo-que-nao-existe", 1000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Calcular_ModeloVazioOuNulo_RetornaNull(string? modelo)
    {
        Assert.Null(OpenAiPricingCalculator.Calcular(modelo!, 1000));
    }

    [Fact]
    public void Calcular_TokensNulos_RetornaNull_NaoZero()
    {
        // Diferente do Anthropic (que trata tokens nulos como 0), aqui tokens é a ÚNICA
        // entrada — sem valor, não dá pra "calcular zero", é ausência de dado mesmo.
        Assert.Null(OpenAiPricingCalculator.Calcular("text-embedding-3-small", tokens: null));
    }

    [Fact]
    public void Calcular_EmbeddingSmallComTokensConhecidos_CalculaCustoExato()
    {
        var custo = OpenAiPricingCalculator.Calcular("text-embedding-3-small", tokens: 1_000_000);

        Assert.Equal(0.02m, custo);
    }

    [Fact]
    public void Calcular_EmbeddingLargeComTokensConhecidos_CalculaCustoExato()
    {
        var custo = OpenAiPricingCalculator.Calcular("text-embedding-3-large", tokens: 500_000);

        Assert.Equal(0.065m, custo);
    }

    [Fact]
    public void Calcular_NomeDeModeloEhCaseInsensitive()
    {
        var custoMinusculo = OpenAiPricingCalculator.Calcular("text-embedding-3-small", 1000);
        var custoMaiusculo = OpenAiPricingCalculator.Calcular("TEXT-EMBEDDING-3-SMALL", 1000);

        Assert.Equal(custoMinusculo, custoMaiusculo);
    }
}
