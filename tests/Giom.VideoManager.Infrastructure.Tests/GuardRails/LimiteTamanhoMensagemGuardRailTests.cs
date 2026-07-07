using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.GuardRails;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.GuardRails;

public class LimiteTamanhoMensagemGuardRailTests
{
    private static LimiteTamanhoMensagemGuardRail CriarGuardRail(int limite) =>
        new(Options.Create(new LimiteTamanhoMensagemOptions { MaximoCaracteres = limite }));

    [Fact]
    public async Task AvaliarAsync_DentroDoLimite_Permite()
    {
        var guardRail = CriarGuardRail(100);
        var resultado = await guardRail.AvaliarAsync(new GuardRailContexto("chat1", new string('x', 100)));

        Assert.True(resultado.Permitido);
    }

    [Fact]
    public async Task AvaliarAsync_UmCaractereAcimaDoLimite_Bloqueia()
    {
        var guardRail = CriarGuardRail(100);
        var resultado = await guardRail.AvaliarAsync(new GuardRailContexto("chat1", new string('x', 101)));

        Assert.False(resultado.Permitido);
        Assert.Contains("101", resultado.Motivo);
        Assert.Contains("100", resultado.Motivo);
    }
}
