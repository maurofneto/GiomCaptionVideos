namespace Giom.VideoManager.Infrastructure.Anthropic;

/// <summary>
/// Injeta os headers de Sessions/Users/Properties do Helicone em cada requisição, lendo o
/// HeliconeSessao anunciado via HeliconeSessionContext logo antes da chamada. Fica na pipeline
/// do HttpClient "anthropic" (via AddHttpMessageHandler) — roda em toda chamada, mas só adiciona
/// headers se algum Claude*Service realmente definiu um contexto (senão seria uma chamada sem
/// Helicone habilitado, e os headers ficariam soltos sem sentido).
/// </summary>
public class HeliconeHeadersHandler(HeliconeSessionContext contexto) : DelegatingHandler
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

            if (sessao.Propriedades is not null)
                foreach (var (chave, valor) in sessao.Propriedades)
                    request.Headers.Add($"Helicone-Property-{chave}", valor);
        }

        return base.SendAsync(request, ct);
    }
}
