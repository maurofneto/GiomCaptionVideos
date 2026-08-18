using System.Threading.Channels;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.WhatsApp;

/// <summary>
/// Ponte entre o modelo PUSH do WhatsApp e o modelo PULL do Domain.
///
/// O <see cref="IChatChannel"/> foi desenhado em cima de <c>OuvirAsync</c> devolvendo um
/// IAsyncEnumerable — o consumidor (ChatBotProcessor) PUXA mensagem. Isso encaixa direto no
/// Telegram, que é long-polling. O WhatsApp Cloud API é o oposto: o Meta EMPURRA um POST no
/// nosso webhook quando chega mensagem (confirmado no Spike #138 — não existe polling).
///
/// Em vez de vazar essa diferença pro Domain (mudando a interface por causa de um canal), a
/// fila em memória aqui absorve a inversão: o endpoint HTTP (#140) publica, o canal consome.
/// Nenhum dos dois lados conhece o outro — só esta abstração no meio.
/// </summary>
public interface IWhatsAppInbox
{
    /// <summary>Chamado pelo endpoint do webhook a cada mensagem válida recebida do Meta.</summary>
    ValueTask PublicarAsync(IncomingMessage mensagem, CancellationToken ct = default);

    /// <summary>Consumido por WhatsAppChatChannel.OuvirAsync — bloqueia até chegar mensagem.</summary>
    IAsyncEnumerable<IncomingMessage> LerAsync(CancellationToken ct = default);
}

public class WhatsAppInbox : IWhatsAppInbox
{
    // Limitada de propósito: se o consumidor morrer, uma fila infinita cresceria até derrubar o
    // processo por memória. Com o volume real (bot de instituto, poucas mensagens por dia) esse
    // teto nunca é alcançado — ele existe pro cenário degradado, não pro normal.
    private const int Capacidade = 1000;

    private readonly Channel<IncomingMessage> _fila = Channel.CreateBounded<IncomingMessage>(
        new BoundedChannelOptions(Capacidade)
        {
            // Espera em vez de descartar: mensagem de aluno não pode sumir em silêncio. Se
            // encher (consumidor travado), o webhook segura a resposta e o Meta reentrega
            // depois — reentrega é recuperável, mensagem perdida não é.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public ValueTask PublicarAsync(IncomingMessage mensagem, CancellationToken ct = default)
        => _fila.Writer.WriteAsync(mensagem, ct);

    public IAsyncEnumerable<IncomingMessage> LerAsync(CancellationToken ct = default)
        => _fila.Reader.ReadAllAsync(ct);
}
