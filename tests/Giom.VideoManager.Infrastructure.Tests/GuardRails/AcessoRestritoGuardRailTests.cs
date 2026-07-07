using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.GuardRails;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.GuardRails;

public class AcessoRestritoGuardRailTests
{
    private static (AcessoRestritoGuardRail GuardRail, Mock<IControleAcessoService> ControleAcesso) CriarGuardRail()
    {
        var controleAcesso = new Mock<IControleAcessoService>();
        var guardRail = new AcessoRestritoGuardRail(controleAcesso.Object, Options.Create(new AcessoRestritoOptions { MensagemBloqueio = "Acesso restrito." }));
        return (guardRail, controleAcesso);
    }

    [Fact]
    public void Nome_e_Fase_sao_fixos()
    {
        var (guardRail, _) = CriarGuardRail();
        Assert.Equal("AcessoRestrito", guardRail.Nome);
        Assert.Equal(GuardRailFase.Entrada, guardRail.Fase);
    }

    [Fact]
    public async Task AvaliarAsync_QuandoAutorizado_Permite()
    {
        var (guardRail, controleAcesso) = CriarGuardRail();
        controleAcesso.Setup(c => c.EstaAutorizadoAsync("chat-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var resultado = await guardRail.AvaliarAsync(new GuardRailContexto("chat-1", "pergunta"));

        Assert.True(resultado.Permitido);
    }

    [Fact]
    public async Task AvaliarAsync_QuandoNaoAutorizado_BloqueiaComMensagemConfigurada()
    {
        var (guardRail, controleAcesso) = CriarGuardRail();
        controleAcesso.Setup(c => c.EstaAutorizadoAsync("chat-2", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var resultado = await guardRail.AvaliarAsync(new GuardRailContexto("chat-2", "pergunta"));

        Assert.False(resultado.Permitido);
        Assert.Equal("Acesso restrito.", resultado.RespostaSubstituta);
        Assert.Contains("chat-2", resultado.Motivo);
    }
}
