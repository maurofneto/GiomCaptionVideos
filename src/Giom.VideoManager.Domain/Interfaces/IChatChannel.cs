namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Canal de mensagens bidirecional (Telegram, WhatsApp, web chat, ...).
/// Implementações traduzem o protocolo do canal para IncomingMessage e vice-versa.
/// </summary>
public interface IChatChannel
{
    IAsyncEnumerable<IncomingMessage> OuvirAsync(CancellationToken ct = default);

    Task ResponderAsync(string chatId, string texto, CancellationToken ct = default);

    /// <summary>
    /// Sinaliza que uma resposta está sendo processada (ex: "digitando..."). Nem todo canal
    /// tem esse recurso — o padrão não faz nada, cada canal implementa se e como quiser.
    /// </summary>
    Task IndicarProcessandoAsync(string chatId, CancellationToken ct = default) => Task.CompletedTask;
}

public record IncomingMessage(string ChatId, string Texto, string? UserName);
