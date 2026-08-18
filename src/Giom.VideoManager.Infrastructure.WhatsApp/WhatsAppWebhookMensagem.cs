using System.Text.Json.Serialization;

namespace Giom.VideoManager.Infrastructure.WhatsApp;

/// <summary>
/// Envelope do que trafega na fila entre a Lambda do webhook e o worker do bot (Task #140).
///
/// Carrega o corpo CRU que a Meta postou, sem interpretar nada: quem entende o formato é o
/// <see cref="WhatsAppWebhookProcessor"/>, do lado do worker. A Lambda fica burra de propósito
/// — ela só confere a assinatura e repassa —, então o dia em que a Meta mudar o formato do
/// payload, só o C# muda; a Lambda continua igual.
///
/// O nome da propriedade no JSON é declarado explicitamente porque o
/// <c>SqsFilaConsumer</c> desserializa com <c>JsonSerializer.Deserialize</c> sem opções, e o
/// System.Text.Json casa nome de propriedade de forma case-SENSITIVE por padrão — sem o
/// atributo, o "payload" minúsculo que a Lambda envia chegaria nulo aqui, em silêncio.
/// </summary>
public record WhatsAppWebhookMensagem(
    [property: JsonPropertyName("payload")] string Payload);
