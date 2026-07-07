using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.GuardRails;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.GuardRails;

/// <summary>
/// Usa FakeTimeProvider (não Task.Delay/Thread.Sleep de verdade) pra testar a janela
/// deslizante deterministicamente — sem isso, testar "o que acontece depois de 60 minutos"
/// exigiria ou esperar 60 minutos de verdade, ou um teste que não prova nada.
/// </summary>
public class RateLimitGuardRailTests
{
    private static (RateLimitGuardRail GuardRail, FakeTimeProvider Tempo) CriarGuardRail(int maximoPorJanela, int janelaMinutos)
    {
        var tempo = new FakeTimeProvider();
        var guardRail = new RateLimitGuardRail(Options.Create(new RateLimitOptions { MaximoPorJanela = maximoPorJanela, JanelaMinutos = janelaMinutos }), tempo);
        return (guardRail, tempo);
    }

    [Fact]
    public async Task AvaliarAsync_AbaixoDoLimite_SempreLibera()
    {
        var (guardRail, _) = CriarGuardRail(maximoPorJanela: 3, janelaMinutos: 60);
        var contexto = new GuardRailContexto("chat1", "pergunta");

        Assert.True((await guardRail.AvaliarAsync(contexto)).Permitido);
        Assert.True((await guardRail.AvaliarAsync(contexto)).Permitido);
        Assert.True((await guardRail.AvaliarAsync(contexto)).Permitido);
    }

    [Fact]
    public async Task AvaliarAsync_AoAtingirOLimiteNaMesmaJanela_Bloqueia()
    {
        var (guardRail, _) = CriarGuardRail(maximoPorJanela: 3, janelaMinutos: 60);
        var contexto = new GuardRailContexto("chat1", "pergunta");

        await guardRail.AvaliarAsync(contexto);
        await guardRail.AvaliarAsync(contexto);
        await guardRail.AvaliarAsync(contexto);
        var resultado = await guardRail.AvaliarAsync(contexto); // a 4ª estoura o limite de 3

        Assert.False(resultado.Permitido);
        Assert.NotNull(resultado.RespostaSubstituta);
    }

    [Fact]
    public async Task AvaliarAsync_ChatsDiferentes_TemJanelasIndependentes()
    {
        var (guardRail, _) = CriarGuardRail(maximoPorJanela: 1, janelaMinutos: 60);

        await guardRail.AvaliarAsync(new GuardRailContexto("chat1", "pergunta"));
        var resultadoChat1 = await guardRail.AvaliarAsync(new GuardRailContexto("chat1", "pergunta"));
        var resultadoChat2 = await guardRail.AvaliarAsync(new GuardRailContexto("chat2", "pergunta"));

        Assert.False(resultadoChat1.Permitido); // chat1 já estourou o próprio limite
        Assert.True(resultadoChat2.Permitido);  // chat2 nunca mandou nada ainda
    }

    [Fact]
    public async Task AvaliarAsync_QuandoAJanelaExpira_VoltaALiberar()
    {
        var (guardRail, tempo) = CriarGuardRail(maximoPorJanela: 1, janelaMinutos: 60);
        var contexto = new GuardRailContexto("chat1", "pergunta");

        await guardRail.AvaliarAsync(contexto);
        Assert.False((await guardRail.AvaliarAsync(contexto)).Permitido); // estourou o limite de 1

        tempo.Advance(TimeSpan.FromMinutes(61)); // passa da janela de 60 minutos

        Assert.True((await guardRail.AvaliarAsync(contexto)).Permitido); // mensagem antiga já saiu da janela
    }
}
