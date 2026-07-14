using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Helicone;

/// <summary>
/// Decide, A CADA REQUISIÇÃO (não na construção do client), se ela passa pelo gateway do
/// Helicone — lendo ISessionContext.Atual.UsarObservabilidade (US #106, estampado por
/// FallbackResumoService/FallbackCategorizacaoService antes de chamar o provedor). Substitui o
/// mecanismo antigo (AnthropicClientFactory decidindo na construção do client, fixo por
/// instância) só para Claude/Grok/OpenAiResumoService/CategorizacaoService — os únicos cuja
/// decisão varia por chamada; ClaudeAgentService/ClaudeAvaliadorIAService continuam com decisão
/// fixa no startup, sem passar por aqui. Nenhum Claude/Grok/OpenAiXxxService sabe que isso existe.
/// </summary>
public sealed class HeliconeRoteamentoHandler(ISessionContext contexto, IOptions<HeliconeOptions> heliconeOptions, HeliconeRota rota) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var helicone = heliconeOptions.Value;
        var sessao = contexto.Atual;

        if (sessao is { UsarObservabilidade: true } && !string.IsNullOrWhiteSpace(helicone.ApiKey) && request.RequestUri is not null)
        {
            var destinoReal = request.RequestUri;

            request.Headers.Add("Helicone-Auth", $"Bearer {helicone.ApiKey}");
            if (!string.IsNullOrWhiteSpace(helicone.Environment))
                request.Headers.Add("Helicone-Property-Environment", helicone.Environment);

            if (rota.ViaGatewayGenerico)
            {
                // Sem domínio dedicado nesse provedor (ex.: xAI/Grok) — o Gateway genérico do
                // Helicone precisa saber pra onde encaminhar de verdade. O Authorization
                // original do provedor (já presente na requisição) continua indo junto.
                request.Headers.Add("Helicone-Target-Url", $"{destinoReal.Scheme}://{destinoReal.Authority}");
                request.Headers.Add("Helicone-Target-Provider", rota.NomeProvedorAlvo!);
            }

            request.RequestUri = new UriBuilder(destinoReal) { Host = rota.HostGateway }.Uri;
        }

        return base.SendAsync(request, ct);
    }
}
