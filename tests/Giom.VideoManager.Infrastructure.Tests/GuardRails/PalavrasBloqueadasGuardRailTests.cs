using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.GuardRails;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.GuardRails;

public class PalavrasBloqueadasGuardRailTests
{
    private static PalavrasBloqueadasGuardRail CriarGuardRail(params string[] palavras) =>
        new(Options.Create(new PalavrasBloqueadasOptions { Palavras = palavras, MensagemBloqueio = "Não posso ajudar com isso." }));

    [Fact]
    public void Nome_e_Fase_sao_fixos()
    {
        var guardRail = CriarGuardRail();
        Assert.Equal("PalavrasBloqueadas", guardRail.Nome);
        Assert.Equal(GuardRailFase.Entrada, guardRail.Fase);
    }

    [Fact]
    public async Task AvaliarAsync_SemPalavrasConfiguradas_NuncaBloqueia()
    {
        var guardRail = CriarGuardRail(); // lista vazia
        var resultado = await guardRail.AvaliarAsync(new GuardRailContexto("chat1", "qualquer coisa por aqui"));

        Assert.True(resultado.Permitido);
    }

    [Theory]
    [InlineData("essa pergunta tem PALAVRA-PROIBIDA no meio", true)]
    [InlineData("essa pergunta tem palavra-proibida no meio", true)] // case-insensitive
    [InlineData("essa pergunta é totalmente inofensiva", false)]
    public async Task AvaliarAsync_ComTermoConfigurado_BloqueiaIndependenteDeCaixa(string pergunta, bool deveriaBloquear)
    {
        var guardRail = CriarGuardRail("palavra-proibida");
        var resultado = await guardRail.AvaliarAsync(new GuardRailContexto("chat1", pergunta));

        Assert.Equal(!deveriaBloquear, resultado.Permitido);
    }

    [Fact]
    public async Task AvaliarAsync_QuandoBloqueia_UsaAMensagemConfigurada()
    {
        var guardRail = CriarGuardRail("proibido");
        var resultado = await guardRail.AvaliarAsync(new GuardRailContexto("chat1", "isso é proibido"));

        Assert.False(resultado.Permitido);
        Assert.Equal("Não posso ajudar com isso.", resultado.RespostaSubstituta);
        Assert.Contains("proibido", resultado.Motivo);
    }
}
