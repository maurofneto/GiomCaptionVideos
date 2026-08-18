using System.Text.Json;
using Xunit;

namespace Giom.VideoManager.Infrastructure.WhatsApp.Tests;

/// <summary>
/// Contrato ENTRE RUNTIMES: a Lambda em Python monta esse JSON (ver
/// infra/lambda-whatsapp-webhook/lambda_function.py) e o SqsFilaConsumer desserializa aqui no
/// C#. Nada no compilador cobre essa fronteira — se os dois lados discordarem do nome do campo,
/// o Payload chega nulo em silêncio e a pergunta do aluno some sem erro nenhum.
///
/// Desserializa com JsonSerializer.Deserialize SEM opções, igual o SqsFilaConsumer faz — é o
/// que torna o teste fiel: com opções relaxadas (case-insensitive) ele passaria mesmo se o
/// atributo JsonPropertyName fosse removido, e deixaria o bug passar.
/// </summary>
public class WhatsAppWebhookMensagemTests
{
    [Fact]
    public void Desserializa_OJsonQueALambdaEnvia()
    {
        // Exatamente o formato de json.dumps({"payload": corpo}) do lado Python.
        var daLambda = """{"payload": "{\"entry\":[{\"id\":\"1\"}]}"}""";

        var mensagem = JsonSerializer.Deserialize<WhatsAppWebhookMensagem>(daLambda);

        Assert.NotNull(mensagem);
        Assert.Equal("""{"entry":[{"id":"1"}]}""", mensagem.Payload);
    }

    [Fact]
    public void Desserializa_MesmoComPayloadGrandeEAcentuado()
    {
        var corpoOriginal = """{"texto":"quem é Melquisedeque? — pergunta do aluno"}""";
        var daLambda = JsonSerializer.Serialize(new Dictionary<string, string> { ["payload"] = corpoOriginal });

        var mensagem = JsonSerializer.Deserialize<WhatsAppWebhookMensagem>(daLambda);

        Assert.Equal(corpoOriginal, mensagem!.Payload);
    }
}
