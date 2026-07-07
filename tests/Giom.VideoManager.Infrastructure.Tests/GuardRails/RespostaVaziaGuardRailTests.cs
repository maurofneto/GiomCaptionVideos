using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.GuardRails;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.GuardRails;

public class RespostaVaziaGuardRailTests
{
    private readonly RespostaVaziaGuardRail _guardRail = new();

    [Fact]
    public void Fase_e_Saida_naoEntrada()
    {
        Assert.Equal(GuardRailFase.Saida, _guardRail.Fase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("curta")] // menos de 10 caracteres
    public async Task AvaliarAsync_RespostaVaziaOuMuitoCurta_Bloqueia(string? resposta)
    {
        var resultado = await _guardRail.AvaliarAsync(new GuardRailContexto("chat1", "pergunta", resposta));

        Assert.False(resultado.Permitido);
        Assert.NotNull(resultado.RespostaSubstituta);
    }

    [Fact]
    public async Task AvaliarAsync_RespostaComConteudoReal_Permite()
    {
        var resultado = await _guardRail.AvaliarAsync(new GuardRailContexto("chat1", "pergunta", "Essa é uma resposta de verdade, com conteúdo."));

        Assert.True(resultado.Permitido);
    }
}
