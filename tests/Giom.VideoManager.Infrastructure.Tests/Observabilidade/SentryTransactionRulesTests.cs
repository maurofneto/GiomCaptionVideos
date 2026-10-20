using Giom.VideoManager.Infrastructure.Observabilidade;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Observabilidade;

/// <summary>
/// Bug #128: regras genéricas de exclusão/redação de telemetria pro Sentry — cada adapter
/// contribui a sua, sem código novo em SentryBootstrap a cada caso.
/// </summary>
public class SentryTransactionRulesTests
{
    [Theory]
    [InlineData("POST https://api.telegram.org/bot123:ABC/getUpdates", true)]
    [InlineData("POST https://api.telegram.org/bot123:ABC/sendMessage", false)]
    public void SubstringSentryTransactionExclusionRule_DeveDescartar_SoQuandoContemOTrecho(string nomeTransacao, bool esperado)
    {
        var regra = new SubstringSentryTransactionExclusionRule("/getUpdates");

        Assert.Equal(esperado, regra.DeveDescartar(nomeTransacao));
    }

    [Fact]
    public void RegexSentryDataScrubber_Redigir_SubstituiOTrechoQueBateOPadrao()
    {
        var scrubber = new RegexSentryDataScrubber(@"/bot\d+:[A-Za-z0-9_-]+", "/bot***");

        var resultado = scrubber.Redigir("https://api.telegram.org/bot123456789:AAFakeTestToken_abc/getUpdates");

        Assert.Equal("https://api.telegram.org/bot***/getUpdates", resultado);
    }

    [Fact]
    public void RegexSentryDataScrubber_Redigir_NaoAlteraTextoSemOPadrao()
    {
        var scrubber = new RegexSentryDataScrubber(@"/bot\d+:[A-Za-z0-9_-]+", "/bot***");

        var resultado = scrubber.Redigir("https://api.telegram.org/sem-token-aqui");

        Assert.Equal("https://api.telegram.org/sem-token-aqui", resultado);
    }
}
