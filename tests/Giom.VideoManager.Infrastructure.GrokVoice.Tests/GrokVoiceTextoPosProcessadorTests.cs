using Xunit;

namespace Giom.VideoManager.Infrastructure.GrokVoice.Tests;

public class GrokVoiceTextoPosProcessadorTests
{
    [Fact]
    public void TextoNuloOuVazio_DevolveVazio()
    {
        Assert.Equal(string.Empty, GrokVoiceTextoPosProcessador.Limpar(null));
        Assert.Equal(string.Empty, GrokVoiceTextoPosProcessador.Limpar("   "));
    }

    [Fact]
    public void TextoNormal_PassaIntacto()
    {
        const string texto = "Tudo começa em casa. Você primeiro tem que fazer funcionar onde está.";
        Assert.Equal(texto, GrokVoiceTextoPosProcessador.Limpar(texto));
    }

    [Theory]
    [InlineData("Música. Boa noite, igreja.")]
    [InlineData("[Música] Boa noite, igreja.")]
    [InlineData("(Música) Boa noite, igreja.")]
    [InlineData("♪ Boa noite, igreja.")]
    public void MarcadorDeMusica_ERemovido(string entrada)
    {
        Assert.Equal("Boa noite, igreja.", GrokVoiceTextoPosProcessador.Limpar(entrada));
    }

    [Fact]
    public void SoMusica_DevolveVazio()
    {
        Assert.Equal(string.Empty, GrokVoiceTextoPosProcessador.Limpar("Música. [Música]. Música."));
    }

    [Fact]
    public void FraseRepetidaEmSequencia_ColapsaNoMaximoPermitido()
    {
        var entrada = string.Join(' ', Enumerable.Repeat("Se inscreva no canal.", 18)) + " Boa noite.";

        var saida = GrokVoiceTextoPosProcessador.Limpar(entrada);

        Assert.Equal("Se inscreva no canal. Se inscreva no canal. Boa noite.", saida);
    }

    [Fact]
    public void MesmaFraseEmLugaresDiferentes_NaoEColapsada()
    {
        const string texto = "Amém. Vamos orar. Amém. Vamos orar. Amém.";
        Assert.Equal(texto, GrokVoiceTextoPosProcessador.Limpar(texto));
    }

    [Fact]
    public void Repeticao_IgnoraMaiusculas()
    {
        var saida = GrokVoiceTextoPosProcessador.Limpar("Amém. AMÉM. amém. Amém. Fim.");
        Assert.Equal("Amém. AMÉM. Fim.", saida);
    }
}
