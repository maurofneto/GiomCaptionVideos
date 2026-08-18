using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.WhatsApp.Tests;

public class WhatsAppChatChannelTests
{
    private static (WhatsAppChatChannel Canal, HttpFake Http) Criar()
    {
        var http = new HttpFake();
        var canal = new WhatsAppChatChannel(
            http.ComoFactory(),
            new WhatsAppInbox(),
            Options.Create(new WhatsAppOptions { PhoneNumberId = "111", AccessToken = "token", ApiVersion = "v22.0" }),
            new FakeLogger<WhatsAppChatChannel>());

        return (canal, http);
    }

    [Fact]
    public async Task ResponderAsync_SemAvaliacaoId_EnviaSoAMensagemDeTexto()
    {
        var (canal, http) = Criar();

        await canal.ResponderAsync("5511999", "resposta curta");

        var corpo = Assert.Single(http.CorposEnviados);
        Assert.Contains("\"type\":\"text\"", corpo);
        Assert.Contains("\"to\":\"5511999\"", corpo);
        Assert.DoesNotContain("interactive", corpo);
    }

    [Fact]
    public async Task ResponderAsync_ComAvaliacaoId_EnviaMensagemInterativaSeparadaComOsDoisBotoes()
    {
        var (canal, http) = Criar();

        await canal.ResponderAsync("5511999", "resposta curta", avaliacaoId: "turno-7");

        Assert.Equal(2, http.CorposEnviados.Count);

        var interativa = http.CorposEnviados[1];
        Assert.Contains("\"type\":\"interactive\"", interativa);
        Assert.Contains("fb:turno-7:1", interativa);
        Assert.Contains("fb:turno-7:-1", interativa);
    }

    [Fact]
    public async Task ResponderAsync_TextoAcimaDoLimite_DivideEmVariasMensagens()
    {
        var (canal, http) = Criar();
        var textoLongo = string.Join("\n\n", Enumerable.Repeat(new string('a', 1000), 8)); // ~8000 chars

        await canal.ResponderAsync("5511999", textoLongo);

        Assert.True(http.CorposEnviados.Count > 1, "texto de ~8000 caracteres deveria virar mais de uma mensagem");
    }

    [Fact]
    public async Task ResponderAsync_MarkdownDoAgente_ChegaConvertidoParaMarcacaoDoWhatsApp()
    {
        var (canal, http) = Criar();

        await canal.ResponderAsync("5511999", "**Gênesis** é o *primeiro* livro");

        var corpo = Assert.Single(http.CorposEnviados);
        Assert.Contains("*Gênesis* é o _primeiro_ livro", corpo);
    }

    [Fact]
    public async Task ResponderAsync_QuandoOEnvioDoTextoFalha_PropagaOErro()
    {
        var (canal, http) = Criar();
        http.FalharQuandoCorpoContiver = "\"type\":\"text\"";

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => canal.ResponderAsync("5511999", "oi"));
        Assert.Contains("5511999", ex.Message);
    }

    /// <summary>
    /// A resposta em si já chegou ao aluno — deixar a falha do botão subir faria o
    /// ChatBotProcessor mandar mensagem de erro por cima de uma interação que deu certo.
    /// </summary>
    [Fact]
    public async Task ResponderAsync_QuandoSoOsBotoesFalham_NaoPropagaOErro()
    {
        var (canal, http) = Criar();
        http.FalharQuandoCorpoContiver = "interactive";

        await canal.ResponderAsync("5511999", "resposta que deu certo", avaliacaoId: "turno-7");

        Assert.Equal(2, http.CorposEnviados.Count);
    }
}
