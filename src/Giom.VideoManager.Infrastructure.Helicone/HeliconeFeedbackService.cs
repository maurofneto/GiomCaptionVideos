using System.Net.Http.Headers;
using System.Text;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Helicone;

/// <summary>
/// POST /v1/request/{id}/feedback — API de feedback do Helicone (não o gateway de proxy de LLM,
/// é a API de management: https://api.helicone.ai). O {id} é o mesmo valor enviado como header
/// Helicone-Request-Id na chamada original (ver SessaoObservabilidade.RequestId/HeliconeHeadersHandler) —
/// gerado por quem chamou a API de IA, não pelo Helicone, exatamente pra poder reportar o voto
/// depois sem precisar ter capturado nada da resposta da Anthropic API.
/// </summary>
public class HeliconeFeedbackService(IOptions<HeliconeOptions> heliconeOptions, IHttpClientFactory httpClientFactory, ILogger<HeliconeFeedbackService> logger) : IFeedbackExternoService
{
    public async Task EnviarFeedbackAsync(string requestId, bool util, CancellationToken ct = default)
    {
        var apiKey = heliconeOptions.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey)) return; // Helicone não configurado — no-op

        try
        {
            var http = httpClientFactory.CreateClient("helicone-api");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var body = new StringContent(JsonConvert.SerializeObject(new { rating = util }), Encoding.UTF8, "application/json");
            var response = await http.PostAsync($"/v1/request/{requestId}/feedback", body, ct);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Helicone recusou o feedback (request {RequestId}): {Status}", requestId, response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Feedback pro Helicone é um "nice to have" — nunca deve derrubar o fluxo de
            // feedback real do usuário (que já foi gravado no Supabase antes desta chamada).
            logger.LogWarning(ex, "Falha ao enviar feedback pro Helicone (request {RequestId})", requestId);
        }
    }
}
