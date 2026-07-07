using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.GuardRails;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.GuardRails;

public class GuardRailIATests
{
    private static (GuardRailIA GuardRail, Mock<IAvaliadorIAService> Avaliador) CriarGuardRail(GuardRailFase fase = GuardRailFase.Entrada)
    {
        var avaliador = new Mock<IAvaliadorIAService>();
        var guardRail = new GuardRailIA(avaliador.Object, Options.Create(new GuardRailIAOptions
        {
            Instrucao = "instrução de teste",
            Fase = fase,
            MensagemBloqueio = "Não posso ajudar com isso por aqui."
        }));

        return (guardRail, avaliador);
    }

    [Fact]
    public async Task AvaliarAsync_QuandoAvaliadorAprova_Permite()
    {
        var (guardRail, avaliador) = CriarGuardRail();
        avaliador.Setup(a => a.AvaliarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AvaliacaoIA(Aprovado: true));

        var resultado = await guardRail.AvaliarAsync(new GuardRailContexto("chat1", "pergunta normal"));

        Assert.True(resultado.Permitido);
    }

    [Fact]
    public async Task AvaliarAsync_QuandoAvaliadorReprova_BloqueiaComMensagemConfiguradaEMotivoDoAvaliador()
    {
        var (guardRail, avaliador) = CriarGuardRail();
        avaliador.Setup(a => a.AvaliarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AvaliacaoIA(Aprovado: false, Motivo: "conteúdo abusivo"));

        var resultado = await guardRail.AvaliarAsync(new GuardRailContexto("chat1", "pergunta suspeita"));

        Assert.False(resultado.Permitido);
        Assert.Equal("Não posso ajudar com isso por aqui.", resultado.RespostaSubstituta);
        Assert.Equal("conteúdo abusivo", resultado.Motivo);
    }

    [Fact]
    public async Task AvaliarAsync_NaFaseEntrada_MandaAPerguntaParaOAvaliador()
    {
        var (guardRail, avaliador) = CriarGuardRail(GuardRailFase.Entrada);
        avaliador.Setup(a => a.AvaliarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AvaliacaoIA(true));

        await guardRail.AvaliarAsync(new GuardRailContexto("chat1", "a pergunta", "a resposta"));

        avaliador.Verify(a => a.AvaliarAsync("instrução de teste", "a pergunta", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AvaliarAsync_NaFaseSaida_MandaARespostaParaOAvaliador()
    {
        var (guardRail, avaliador) = CriarGuardRail(GuardRailFase.Saida);
        avaliador.Setup(a => a.AvaliarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AvaliacaoIA(true));

        await guardRail.AvaliarAsync(new GuardRailContexto("chat1", "a pergunta", "a resposta"));

        avaliador.Verify(a => a.AvaliarAsync("instrução de teste", "a resposta", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Fase_RefleteAConfiguracao()
    {
        var (guardRailEntrada, _) = CriarGuardRail(GuardRailFase.Entrada);
        var (guardRailSaida, _) = CriarGuardRail(GuardRailFase.Saida);

        Assert.Equal(GuardRailFase.Entrada, guardRailEntrada.Fase);
        Assert.Equal(GuardRailFase.Saida, guardRailSaida.Fase);
    }
}
