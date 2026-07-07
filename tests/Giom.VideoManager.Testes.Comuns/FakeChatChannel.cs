using System.Runtime.CompilerServices;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Testes.Comuns;

/// <summary>
/// Fake de IChatChannel — grava tudo que passou por ResponderAsync/IndicarProcessandoAsync
/// pra assercao nos testes, sem precisar mockar nenhum SDK de canal (Telegram, WhatsApp...).
/// OuvirAsync devolve a fila configurada em <see cref="Mensagens"/> — cada teste popula o que
/// precisa antes de chamar RunAsync.
/// </summary>
public class FakeChatChannel : IChatChannel
{
    public List<IncomingMessage> Mensagens { get; } = [];
    public List<(string ChatId, string Texto, string? AvaliacaoId)> Respostas { get; } = [];
    public int IndicacoesProcessando { get; private set; }

    /// <summary>Atrasa cada mensagem entregue por RunAsync — usado pra testar cancelamento (ver ChatBotProcessor.RunAsync).</summary>
    public TimeSpan AtrasoEntreMensagens { get; set; } = TimeSpan.Zero;

    public async IAsyncEnumerable<IncomingMessage> OuvirAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var mensagem in Mensagens)
        {
            if (AtrasoEntreMensagens > TimeSpan.Zero)
                await Task.Delay(AtrasoEntreMensagens, ct);

            yield return mensagem;
        }
    }

    public Task ResponderAsync(string chatId, string texto, string? avaliacaoId = null, CancellationToken ct = default)
    {
        Respostas.Add((chatId, texto, avaliacaoId));
        return Task.CompletedTask;
    }

    public Task IndicarProcessandoAsync(string chatId, CancellationToken ct = default)
    {
        IndicacoesProcessando++;
        return Task.CompletedTask;
    }
}
