using System.Diagnostics;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Cada retry do Polly (AddStandardResilienceHandler) só existia como log solto do logger
/// "Polly" — pra correlacionar com o vídeo/etapa que estava sendo processado, era preciso cruzar
/// timestamps manualmente no CloudWatch (foi assim que o Bug #122 precisou ser investigado).
/// Anexa um evento ao Activity ambiente (a mesma etapa aberta via ITracingService.IniciarEtapaAtual)
/// a cada tentativa — aparece dentro do próprio Span no Sentry, já com correlation.id/VideoId
/// (tags de sessão aplicadas no Activity pai) sem precisar de nenhum código extra nos serviços
/// concretos (ClaudeResumoService, VoyageEmbeddingService, etc.).
/// </summary>
public static class ResilienciaTelemetriaExtensions
{
    public static void RegistrarEventoDeRetryNoSpan(this HttpStandardResilienceOptions options, string provedor)
    {
        options.Retry.OnRetry = args =>
        {
            var statusCode = args.Outcome.Result?.StatusCode;
            Activity.Current?.AddEvent(new ActivityEvent("http.retry", tags: new ActivityTagsCollection
            {
                ["provedor"] = provedor,
                ["tentativa"] = args.AttemptNumber,
                ["status_code"] = statusCode.HasValue ? (int)statusCode.Value : null,
                ["exception"] = args.Outcome.Exception?.GetType().Name
            }));
            return ValueTask.CompletedTask;
        };
    }
}
