namespace Domain.Interfaces;

/// <summary>
/// Canal de mensagens bidirecional (Telegram, WhatsApp, web chat, ...).
/// Implementações traduzem o protocolo do canal para IncomingMessage e vice-versa.
/// </summary>
public interface IChatChannel
{
    IAsyncEnumerable<IncomingMessage> OuvirAsync(CancellationToken ct = default);

    Task ResponderAsync(string chatId, string texto, CancellationToken ct = default);
}

public record IncomingMessage(string ChatId, string Texto, string? UserName);
