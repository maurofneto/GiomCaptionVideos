using System.Net;

namespace Giom.VideoManager.Infrastructure.WhatsApp.Tests;

/// <summary>
/// Handler que não vai à rede: guarda o corpo de cada POST pro teste inspecionar o payload
/// exato que teria sido enviado ao Meta. É o único jeito de cobrir a montagem do JSON da Cloud
/// API sem token nem chamada real.
/// </summary>
public class HttpFake : HttpMessageHandler
{
    public List<string> CorposEnviados { get; } = [];
    public HttpStatusCode StatusDeResposta { get; set; } = HttpStatusCode.OK;

    /// <summary>Quando definido, só as chamadas cujo corpo contém este trecho falham.</summary>
    public string? FalharQuandoCorpoContiver { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var corpo = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        CorposEnviados.Add(corpo);

        var falhar = FalharQuandoCorpoContiver is { } trecho
            ? corpo.Contains(trecho, StringComparison.Ordinal)
            : StatusDeResposta != HttpStatusCode.OK;

        return new HttpResponseMessage(falhar ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
        {
            Content = new StringContent(falhar ? """{"error":{"message":"falha simulada"}}""" : """{"messages":[{"id":"wamid.fake"}]}""")
        };
    }

    public IHttpClientFactory ComoFactory() => new FactoryFake(this);

    private sealed class FactoryFake(HttpFake handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://graph.facebook.com/") };
    }
}
