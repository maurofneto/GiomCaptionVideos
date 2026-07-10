using Giom.VideoManager.Infrastructure.YouTube;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.YouTube;

/// <summary>
/// US #105 — bug real: ApplicationName vira header HTTP (User-Agent) do cliente Google, que
/// rejeita acento com FormatException ("Ministério Giom Processor..." quebrou a credencial do
/// canal Ministério na primeira validação em dev). ParaAscii remove o diacrítico preservando o
/// nome, em vez de trocar por um slug.
/// </summary>
public class ApplicationNameFormatterTests
{
    [Theory]
    [InlineData("Ministério Giom", "Ministerio Giom")]
    [InlineData("Instituto Giom", "Instituto Giom")]
    [InlineData("Canal com Ção e Àçãí", "Canal com Cao e Acai")]
    [InlineData("", "")]
    public void ParaAscii_RemoveDiacriticosPreservandoONome(string nome, string esperado)
    {
        Assert.Equal(esperado, ApplicationNameFormatter.ParaAscii(nome));
    }

    [Fact]
    public void ParaAscii_ResultadoSoContemCaracteresAscii()
    {
        var resultado = ApplicationNameFormatter.ParaAscii("Ministério Giom");

        Assert.All(resultado, c => Assert.True(c <= 127));
    }
}
