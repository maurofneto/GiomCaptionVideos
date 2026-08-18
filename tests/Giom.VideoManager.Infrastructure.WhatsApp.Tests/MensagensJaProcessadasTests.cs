using Xunit;

namespace Giom.VideoManager.Infrastructure.WhatsApp.Tests;

public class MensagensJaProcessadasTests
{
    [Fact]
    public void RegistrarSeNova_PrimeiraVez_DevolveTrue_ERepetida_DevolveFalse()
    {
        var registro = new MensagensJaProcessadas();

        Assert.True(registro.RegistrarSeNova("wamid.abc"));
        Assert.False(registro.RegistrarSeNova("wamid.abc"));
    }

    [Fact]
    public void RegistrarSeNova_AlemDaCapacidade_EsqueceOsMaisAntigos()
    {
        var registro = new MensagensJaProcessadas();
        for (var i = 0; i < 600; i++) registro.RegistrarSeNova($"wamid.{i}");

        // O primeiro já saiu da janela (capacidade 500) — volta a ser tratado como novo.
        Assert.True(registro.RegistrarSeNova("wamid.0"));
        // Um recente continua bloqueado.
        Assert.False(registro.RegistrarSeNova("wamid.599"));
    }
}
