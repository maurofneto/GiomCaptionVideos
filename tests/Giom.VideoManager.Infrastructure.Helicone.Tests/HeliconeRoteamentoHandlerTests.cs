using System.Net;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Helicone.Tests;

file sealed class FakeSessionContext : ISessionContext
{
    public SessaoObservabilidade? Atual { get; private set; }
    public void Definir(SessaoObservabilidade sessao) => Atual = sessao;
}

file sealed class HandlerCapturaRequisicao(Action<HttpRequestMessage> capturar) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        capturar(request);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

/// <summary>US #106 — nenhum Claude/Grok/OpenAiXxxService decide isso: o roteamento pro gateway do Helicone é decidido por chamada, lendo só o ISessionContext ambiente.</summary>
public class HeliconeRoteamentoHandlerTests
{
    private static IOptions<HeliconeOptions> ComApiKey(string apiKey = "helicone-key") =>
        Options.Create(new HeliconeOptions { ApiKey = apiKey, Environment = "dev" });

    [Fact]
    public async Task SendAsync_UsarObservabilidadeTrue_DominioDedicado_TrocaHostEAdicionaAuth()
    {
        var contexto = new FakeSessionContext();
        contexto.Definir(new SessaoObservabilidade("v1", "Video 1", "/pipeline/resumo", UsarObservabilidade: true));

        HttpRequestMessage? capturado = null;
        var handler = new HeliconeRoteamentoHandler(contexto, ComApiKey(), HeliconeRota.DominioDedicado("anthropic.helicone.ai"))
        { InnerHandler = new HandlerCapturaRequisicao(r => capturado = r) };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://api.anthropic.com/v1/messages");

        Assert.Equal("anthropic.helicone.ai", capturado!.RequestUri!.Host);
        Assert.Equal("/v1/messages", capturado.RequestUri.AbsolutePath);
        Assert.Equal("Bearer helicone-key", capturado.Headers.GetValues("Helicone-Auth").Single());
        Assert.Equal("dev", capturado.Headers.GetValues("Helicone-Property-Environment").Single());
        Assert.False(capturado.Headers.Contains("Helicone-Target-Url"));
    }

    [Fact]
    public async Task SendAsync_UsarObservabilidadeTrue_GatewayGenerico_AdicionaTargetUrlETargetProvider()
    {
        var contexto = new FakeSessionContext();
        contexto.Definir(new SessaoObservabilidade("v1", "Video 1", "/pipeline/resumo", UsarObservabilidade: true));

        HttpRequestMessage? capturado = null;
        var handler = new HeliconeRoteamentoHandler(contexto, ComApiKey(), HeliconeRota.GatewayGenerico("X-AI"))
        { InnerHandler = new HandlerCapturaRequisicao(r => capturado = r) };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://api.x.ai/v1/chat/completions");

        Assert.Equal("gateway.helicone.ai", capturado!.RequestUri!.Host);
        Assert.Equal("https://api.x.ai", capturado.Headers.GetValues("Helicone-Target-Url").Single());
        Assert.Equal("X-AI", capturado.Headers.GetValues("Helicone-Target-Provider").Single());
    }

    [Fact]
    public async Task SendAsync_UsarObservabilidadeFalse_NaoAlteraRequisicao()
    {
        var contexto = new FakeSessionContext();
        contexto.Definir(new SessaoObservabilidade("v1", "Video 1", "/pipeline/resumo", UsarObservabilidade: false));

        HttpRequestMessage? capturado = null;
        var handler = new HeliconeRoteamentoHandler(contexto, ComApiKey(), HeliconeRota.DominioDedicado("anthropic.helicone.ai"))
        { InnerHandler = new HandlerCapturaRequisicao(r => capturado = r) };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://api.anthropic.com/v1/messages");

        Assert.Equal("api.anthropic.com", capturado!.RequestUri!.Host);
        Assert.False(capturado.Headers.Contains("Helicone-Auth"));
    }

    [Fact]
    public async Task SendAsync_UsarObservabilidadeTrue_SemApiKeyConfigurada_NaoAlteraRequisicao()
    {
        var contexto = new FakeSessionContext();
        contexto.Definir(new SessaoObservabilidade("v1", "Video 1", "/pipeline/resumo", UsarObservabilidade: true));

        HttpRequestMessage? capturado = null;
        var handler = new HeliconeRoteamentoHandler(contexto, ComApiKey(apiKey: ""), HeliconeRota.DominioDedicado("anthropic.helicone.ai"))
        { InnerHandler = new HandlerCapturaRequisicao(r => capturado = r) };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://api.anthropic.com/v1/messages");

        Assert.Equal("api.anthropic.com", capturado!.RequestUri!.Host);
    }
}
