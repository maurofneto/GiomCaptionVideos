using Giom.VideoManager.Infrastructure.Sqs;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Sqs;

/// <summary>
/// Bug #133: ReceiveMessage do SQS em long-poll contínuo (5 filas) sozinho já respondia por
/// mais spans que todas as etapas de pipeline somadas — mesma causa raiz do polling do
/// Telegram (Bug #128), sem segredo na URL (SQS autentica via header, não via URL).
/// </summary>
public class SqsSentryTransactionExclusionRuleTests
{
    [Theory]
    [InlineData("POST https://sqs.us-east-1.amazonaws.com/", true)]
    [InlineData("POST https://sqs.sa-east-1.amazonaws.com/", true)]
    [InlineData("POST https://api.telegram.org/bot123/getUpdates", false)]
    [InlineData("pipeline.orquestracao", false)]
    public void DeveDescartar_SoQuandoONomeContemSqs(string nomeTransacao, bool esperado)
    {
        var regra = new SqsSentryTransactionExclusionRule();

        Assert.Equal(esperado, regra.DeveDescartar(nomeTransacao));
    }
}
