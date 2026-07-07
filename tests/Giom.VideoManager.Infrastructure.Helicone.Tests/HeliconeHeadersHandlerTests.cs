using System.Net;
using Giom.VideoManager.Domain.Interfaces;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Helicone.Tests;

file sealed class FakeSessionContext : ISessionContext
{
    public SessaoObservabilidade? Atual { get; private set; }
    public void Definir(SessaoObservabilidade sessao) => Atual = sessao;
}

file sealed class HandlerFinalQueSempreRespondeOk : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
}

file sealed class HandlerCapturaRequisicao(Action<HttpRequestMessage> capturar) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        capturar(request);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

public class HeliconeHeadersHandlerTests
{
    [Theory]
    [InlineData("Instituto Teológico Giom", "Instituto Teologico Giom")]
    [InlineData("Escatologia — Parte 1", "Escatologia  Parte 1")] // em-dash não é ASCII nem acento — só some
    [InlineData("já sem acento", "ja sem acento")]
    [InlineData("plain ascii text", "plain ascii text")]
    [InlineData("", "")]
    public void ParaAscii_RemoveAcentosEQualquerCaractereNaoAscii(string entrada, string esperado)
    {
        Assert.Equal(esperado, HeliconeHeadersHandler.ParaAscii(entrada));
    }

    [Fact]
    public async Task SendAsync_ComTituloDeVideoAcentuadoNaSessao_NuncaLancaExcecaoAoMontarOsHeaders()
    {
        // Bug real de produção (2026-07-07): SessionName = video.Title acentuado quebrava TODA
        // chamada ao Claude via Helicone com HttpRequestException "headers must contain only
        // ASCII characters" (o próprio HttpClient rejeita o header, sem chegar a fazer nenhuma
        // chamada de rede) — quase todo resumo do Instituto falhava. Este teste existe pra
        // nunca regredir.
        var contexto = new FakeSessionContext();
        contexto.Definir(new SessaoObservabilidade(
            SessionId: "video-123",
            SessionName: "Escatologia: A Ressurreição e o Juízo Final",
            SessionPath: "/pipeline/resumo",
            Propriedades: new Dictionary<string, string> { ["Canal"] = "Instituto Teológico Giom" }));

        var handler = new HeliconeHeadersHandler(contexto) { InnerHandler = new HandlerFinalQueSempreRespondeOk() };
        using var client = new HttpClient(handler);

        var excecao = await Record.ExceptionAsync(() => client.GetAsync("https://api.anthropic.com/v1/messages"));

        Assert.Null(excecao);
    }

    [Fact]
    public async Task SendAsync_ComSessaoDefinida_AnexaOsHeadersEsperados()
    {
        var contexto = new FakeSessionContext();
        contexto.Definir(new SessaoObservabilidade(
            SessionId: "video-123",
            SessionName: "Nome sem acento",
            SessionPath: "/pipeline/resumo",
            UserId: "user-1",
            RequestId: "req-1",
            Propriedades: new Dictionary<string, string> { ["Tipo"] = "resumo" }));

        HttpRequestMessage? capturado = null;
        var handler = new HeliconeHeadersHandler(contexto) { InnerHandler = new HandlerCapturaRequisicao(r => capturado = r) };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://api.anthropic.com/v1/messages");

        Assert.Equal("video-123", capturado!.Headers.GetValues("Helicone-Session-Id").Single());
        Assert.Equal("Nome sem acento", capturado.Headers.GetValues("Helicone-Session-Name").Single());
        Assert.Equal("user-1", capturado.Headers.GetValues("Helicone-User-Id").Single());
        Assert.Equal("req-1", capturado.Headers.GetValues("Helicone-Request-Id").Single());
        Assert.Equal("resumo", capturado.Headers.GetValues("Helicone-Property-Tipo").Single());
    }

    [Fact]
    public async Task SendAsync_SemSessaoDefinida_NaoAdicionaHeaderNenhum()
    {
        var contexto = new FakeSessionContext();
        HttpRequestMessage? capturado = null;
        var handler = new HeliconeHeadersHandler(contexto) { InnerHandler = new HandlerCapturaRequisicao(r => capturado = r) };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://api.anthropic.com/v1/messages");

        Assert.False(capturado!.Headers.Contains("Helicone-Session-Id"));
    }
}
