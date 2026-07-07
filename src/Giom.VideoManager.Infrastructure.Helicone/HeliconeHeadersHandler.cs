using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Helicone;

/// <summary>
/// Traduz o SessaoObservabilidade genérico (ISessionContext) — anunciado por quem chama a API,
/// sem saber que é o Helicone quem está do outro lado — em headers HTTP específicos do
/// Helicone, em cada requisição. Fica na pipeline do HttpClient "anthropic" (via
/// AddHttpMessageHandler) — roda em toda chamada, mas só adiciona headers se algum contexto de
/// sessão realmente foi definido.
/// </summary>
public class HeliconeHeadersHandler(ISessionContext contexto) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var sessao = contexto.Atual;
        if (sessao is not null)
        {
            request.Headers.Add("Helicone-Session-Id", sessao.SessionId);
            request.Headers.Add("Helicone-Session-Name", sessao.SessionName);
            request.Headers.Add("Helicone-Session-Path", sessao.SessionPath);

            if (sessao.UserId is not null)
                request.Headers.Add("Helicone-User-Id", sessao.UserId);

            // Id gerado por quem chamou (não pelo Helicone) — permite reportar feedback depois via
            // POST /v1/request/{id}/feedback sem depender de capturar algo da resposta.
            if (sessao.RequestId is not null)
                request.Headers.Add("Helicone-Request-Id", sessao.RequestId);

            if (sessao.Propriedades is not null)
                foreach (var (chave, valor) in sessao.Propriedades)
                    request.Headers.Add($"Helicone-Property-{chave}", valor);
        }

        return base.SendAsync(request, ct);
    }
}
