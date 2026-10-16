using Giom.VideoManager.Domain.Interfaces;
using Xunit;

namespace Giom.VideoManager.Infrastructure.WhatsApp.Tests;

public class WhatsAppInboxTests
{
    [Fact]
    public async Task PublicarEntaoLer_EntregaAMensagemNaOrdem()
    {
        var inbox = new WhatsAppInbox();
        await inbox.PublicarAsync(new IncomingMessage("5511", "primeira", "Aluno"));
        await inbox.PublicarAsync(new IncomingMessage("5511", "segunda", "Aluno"));

        var recebidas = new List<string>();
        await foreach (var m in inbox.LerAsync())
        {
            recebidas.Add(m.Texto);
            if (recebidas.Count == 2) break;
        }

        Assert.Equal(["primeira", "segunda"], recebidas);
    }

    /// <summary>
    /// A inversão push→pull só funciona se LerAsync ESPERAR mensagem chegar em vez de devolver
    /// vazio — é o que deixa o ChatBotProcessor tratar o WhatsApp igual trata o Telegram.
    /// </summary>
    [Fact]
    public async Task LerAsync_ComFilaVazia_AguardaAtePublicarem()
    {
        var inbox = new WhatsAppInbox();

        var leitura = Task.Run(async () =>
        {
            await foreach (var m in inbox.LerAsync()) return m.Texto;
            return null;
        });

        Assert.False(leitura.IsCompleted, "não deveria ter concluído antes de existir mensagem");

        await inbox.PublicarAsync(new IncomingMessage("5511", "chegou depois", null));

        Assert.Equal("chegou depois", await leitura);
    }
}
