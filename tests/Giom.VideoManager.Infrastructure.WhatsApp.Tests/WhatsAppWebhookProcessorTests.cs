using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Giom.VideoManager.Infrastructure.WhatsApp.Tests;

public class WhatsAppWebhookProcessorTests
{
    private sealed class Ambiente
    {
        public HttpFake Http { get; } = new();
        public WhatsAppInbox Inbox { get; } = new();
        public Mock<IBotComandosService> Comandos { get; } = new();
        public FakeConversaHistoricoService Historico { get; } = new();

        public WhatsAppWebhookProcessor CriarProcessador()
        {
            var canal = new WhatsAppChatChannel(
                Http.ComoFactory(),
                Inbox,
                Options.Create(new WhatsAppOptions { PhoneNumberId = "111", AccessToken = "t" }),
                new FakeLogger<WhatsAppChatChannel>());

            return new WhatsAppWebhookProcessor(canal, Inbox, Comandos.Object, Historico, new FakeLogger<WhatsAppWebhookProcessor>());
        }

        /// <summary>Lê o que estiver na fila agora, sem bloquear se estiver vazia.</summary>
        public async Task<List<IncomingMessage>> DrenarInboxAsync()
        {
            var recebidas = new List<IncomingMessage>();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try
            {
                await foreach (var m in Inbox.LerAsync(cts.Token)) recebidas.Add(m);
            }
            catch (OperationCanceledException) { }
            return recebidas;
        }
    }

    private static string PayloadTexto(string mensagemId, string texto) => $$"""
    {
      "entry": [{
        "changes": [{
          "field": "messages",
          "value": {
            "contacts": [{ "profile": { "name": "Aluno Giom" }, "wa_id": "5511999" }],
            "messages": [{ "from": "5511999", "id": "{{mensagemId}}", "type": "text", "text": { "body": "{{texto}}" } }]
          }
        }]
      }]
    }
    """;

    [Fact]
    public async Task ProcessarAsync_MensagemDeTexto_PublicaNoInboxComNomeDoContato()
    {
        var ambiente = new Ambiente();

        await ambiente.CriarProcessador().ProcessarAsync(PayloadTexto("wamid.1", "quem foi Abraão?"));

        var mensagem = Assert.Single(await ambiente.DrenarInboxAsync());
        Assert.Equal("5511999", mensagem.ChatId);
        Assert.Equal("quem foi Abraão?", mensagem.Texto);
        Assert.Equal("Aluno Giom", mensagem.UserName);
    }

    /// <summary>
    /// O Meta reentrega webhook que não recebeu 200 a tempo — sem a trava de dedup isso viraria
    /// outra chamada ao agente (resposta duplicada + custo de LLM duplicado) pela mesma pergunta.
    /// </summary>
    [Fact]
    public async Task ProcessarAsync_MesmaMensagemEntregueDuasVezes_ProcessaUmaSoVez()
    {
        var ambiente = new Ambiente();
        var processador = ambiente.CriarProcessador();

        await processador.ProcessarAsync(PayloadTexto("wamid.repetida", "oi"));
        await processador.ProcessarAsync(PayloadTexto("wamid.repetida", "oi"));

        Assert.Single(await ambiente.DrenarInboxAsync());
    }

    [Fact]
    public async Task ProcessarAsync_NotificacaoDeStatusSemMensagem_NaoPublicaNadaNemQuebra()
    {
        var ambiente = new Ambiente();
        var payload = """
        { "entry": [{ "changes": [{ "field": "messages", "value": { "statuses": [{ "id": "wamid.x", "status": "delivered" }] } }] }] }
        """;

        await ambiente.CriarProcessador().ProcessarAsync(payload);

        Assert.Empty(await ambiente.DrenarInboxAsync());
    }

    /// <summary>
    /// Lançar aqui faria o endpoint devolver erro, o Meta reentregar o mesmo payload ruim e o
    /// ciclo se repetir pra sempre.
    /// </summary>
    [Fact]
    public async Task ProcessarAsync_JsonInvalido_NaoLanca()
    {
        var ambiente = new Ambiente();

        await ambiente.CriarProcessador().ProcessarAsync("isso não é json");

        Assert.Empty(await ambiente.DrenarInboxAsync());
    }

    [Fact]
    public async Task ProcessarAsync_CliqueNoBotaoDeFeedback_RegistraNoHistoricoENaoVaiProInbox()
    {
        var ambiente = new Ambiente();
        var payload = """
        {
          "entry": [{ "changes": [{ "value": { "messages": [{
            "from": "5511999", "id": "wamid.fb", "type": "interactive",
            "interactive": { "type": "button_reply", "button_reply": { "id": "fb:turno-42:1", "title": "👍 Útil" } }
          }] } }] }]
        }
        """;

        await ambiente.CriarProcessador().ProcessarAsync(payload);

        var (conversaId, util) = Assert.Single(ambiente.Historico.Feedbacks);
        Assert.Equal("turno-42", conversaId);
        Assert.True(util);
        Assert.Empty(await ambiente.DrenarInboxAsync());
    }

    [Fact]
    public async Task ProcessarAsync_Comando_RespondeDiretoENaoVaiProInbox()
    {
        var ambiente = new Ambiente();
        ambiente.Comandos.Setup(c => c.ObterAjuda()).Returns("lista de comandos");

        await ambiente.CriarProcessador().ProcessarAsync(PayloadTexto("wamid.cmd", "/ajuda"));

        Assert.Empty(await ambiente.DrenarInboxAsync());
        Assert.Contains("lista de comandos", Assert.Single(ambiente.Http.CorposEnviados));
    }

    [Fact]
    public async Task ProcessarAsync_MensagemDeAudio_AvisaQueSoEntendeTexto()
    {
        var ambiente = new Ambiente();
        var payload = """
        { "entry": [{ "changes": [{ "value": { "messages": [{ "from": "5511999", "id": "wamid.audio", "type": "audio" }] } }] }] }
        """;

        await ambiente.CriarProcessador().ProcessarAsync(payload);

        Assert.Contains("só consigo ler mensagens de texto", Assert.Single(ambiente.Http.CorposEnviados));
        Assert.Empty(await ambiente.DrenarInboxAsync());
    }
}
