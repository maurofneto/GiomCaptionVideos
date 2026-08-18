using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Giom.VideoManager.Infrastructure.WhatsApp.Tests;

public class WhatsAppWebhookConsumidorTests
{
    private static (WhatsAppWebhookConsumidor Consumidor, WhatsAppInbox Inbox, FakeFilaConsumer<WhatsAppWebhookMensagem> Fila) Criar()
    {
        var inbox = new WhatsAppInbox();
        var fila = new FakeFilaConsumer<WhatsAppWebhookMensagem>();

        var canal = new WhatsAppChatChannel(
            new HttpFake().ComoFactory(), inbox,
            Options.Create(new WhatsAppOptions { PhoneNumberId = "111", AccessToken = "t" }),
            new FakeLogger<WhatsAppChatChannel>());

        var processador = new WhatsAppWebhookProcessor(
            canal, inbox, new Mock<Giom.VideoManager.Domain.Interfaces.IBotComandosService>().Object,
            new FakeConversaHistoricoService(), new FakeLogger<WhatsAppWebhookProcessor>());

        return (new WhatsAppWebhookConsumidor(fila, processador, new FakeLogger<WhatsAppWebhookConsumidor>()), inbox, fila);
    }

    [Fact]
    public async Task ExecutarAsync_EntregaOPayloadDaFilaAoProcessador()
    {
        var (consumidor, inbox, fila) = Criar();
        fila.MensagensParaConsumir.Add(new WhatsAppWebhookMensagem("""
        { "entry": [{ "changes": [{ "value": {
            "contacts": [{ "profile": { "name": "Aluno" }, "wa_id": "5511999" }],
            "messages": [{ "from": "5511999", "id": "wamid.1", "type": "text", "text": { "body": "quem foi Rute?" } }]
        } }] }] }
        """));

        using var cts = new CancellationTokenSource();
        var execucao = consumidor.ExecutarAsync(cts.Token);

        var recebida = await LerUmaAsync(inbox);
        await cts.CancelAsync();
        await execucao;

        Assert.Equal("quem foi Rute?", recebida.Texto);
        Assert.Equal("5511999", recebida.ChatId);
    }

    /// <summary>
    /// O SqsFilaConsumer real RETORNA quando a fila fica vazia pelo grace period (ele foi feito
    /// pros workers que morrem depois de drenar). Num bot, que fica de pé, isso pararia de
    /// escutar o WhatsApp poucos minutos depois do deploy — sem nenhum erro, porque o processo
    /// continua vivo atendendo o Telegram. Este teste trava o laço que reabre a drenagem.
    /// </summary>
    [Fact]
    public async Task ExecutarAsync_QuandoAFilaEsvazia_VoltaAConsumirEmVezDeEncerrar()
    {
        var (consumidor, _, fila) = Criar();

        using var cts = new CancellationTokenSource();
        var execucao = consumidor.ExecutarAsync(cts.Token);

        // A fila fake retorna na hora (vazia). Se não houvesse laço, a execução terminaria
        // sozinha aqui — sem nada ter cancelado.
        await Task.Delay(50);
        Assert.False(execucao.IsCompleted, "o consumidor deveria continuar tentando, não encerrar com a fila vazia");

        await cts.CancelAsync();
        await execucao;
        Assert.True(execucao.IsCompleted);
    }

    private static async Task<Giom.VideoManager.Domain.Interfaces.IncomingMessage> LerUmaAsync(WhatsAppInbox inbox)
    {
        await foreach (var m in inbox.LerAsync()) return m;
        throw new InvalidOperationException("nada chegou ao inbox");
    }
}
