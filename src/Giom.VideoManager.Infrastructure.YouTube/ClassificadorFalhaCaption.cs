using Giom.VideoManager.Domain.Exceptions;
using Google.Apis.Auth.OAuth2.Responses;
using Polly.CircuitBreaker;

namespace Giom.VideoManager.Infrastructure.YouTube;

/// <summary>Resultado da classificação. <see cref="Reason"/>/<see cref="HttpStatus"/> são null quando não há.</summary>
public sealed record FalhaCaptionClassificada(ClasseFalhaCaption Classe, int? HttpStatus = null, string? Reason = null);

/// <summary>
/// Classificador puro (sem I/O, sem log): status HTTP + <c>reason</c> da Google API, erro OAuth ou
/// stderr do yt-dlp → <see cref="ClasseFalhaCaption"/>. A classe de um 403 sai do <c>reason</c>,
/// nunca só do status.
/// </summary>
public static class ClassificadorFalhaCaption
{
    private static readonly string[] ReasonsQuota = ["quotaExceeded", "dailyLimitExceeded"];
    private static readonly string[] ReasonsRateLimit = ["rateLimitExceeded", "userRateLimitExceeded"];
    private static readonly string[] ErrosOAuthCredencial = ["invalid_grant", "invalid_client", "unauthorized_client"];

    public const string ReasonCircuitoAberto = "circuitBreakerOpen";

    /// <summary>
    /// Classifica a exceção (percorrendo InnerException). Null = não reconhecida; cancelamento
    /// pedido pelo chamador nunca é classificado.
    /// </summary>
    public static FalhaCaptionClassificada? Classificar(Exception ex, CancellationToken ct = default)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
            return null;

        for (var atual = ex; atual is not null; atual = atual.InnerException)
        {
            switch (atual)
            {
                case CaptionFalhaException f:
                    return new FalhaCaptionClassificada(f.Classe, f.HttpStatus, f.Reason);

                case RateLimitExcedidoException:
                    // Já convertida pelo Resilient (quotaExceeded ou circuito aberto); status/reason vêm da causa.
                    var causa = atual.InnerException is null ? null : Classificar(atual.InnerException, ct);
                    return new FalhaCaptionClassificada(ClasseFalhaCaption.QuotaExcedida, causa?.HttpStatus,
                        causa?.Reason ?? (ProcurarNaCadeia<BrokenCircuitException>(atual) is not null ? ReasonCircuitoAberto : null));

                case BrokenCircuitException:
                    return new FalhaCaptionClassificada(ClasseFalhaCaption.QuotaExcedida, null, ReasonCircuitoAberto);

                case CredencialCanalIndisponivelException c:
                    return new FalhaCaptionClassificada(ClasseFalhaCaption.CredencialInvalida, null, c.Motivo);

                case TokenResponseException t:
                    return ClassificarToken(t);

                case Google.GoogleApiException g:
                    return ClassificarGoogleApi(g);

                case TaskCanceledException or TimeoutException or HttpRequestException:
                    return new FalhaCaptionClassificada(ClasseFalhaCaption.Transitorio, null, atual.GetType().Name);
            }
        }

        return null;
    }

    public static bool EhQuota(Google.GoogleApiException ex) =>
        ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden && Reasons(ex).Any(r => ReasonsQuota.Contains(r));

    /// <summary>
    /// Decisão de falha do yt-dlp: só classifica com exit ≠ 0. Com exit 0 o texto de bloqueio pode ser
    /// aviso de tentativa intermediária (o processo se recuperou) — não é falha, o fluxo segue normal.
    /// </summary>
    public static FalhaCaptionClassificada? ClassificarSaidaYtDlp(int exitCode, string? stderr) =>
        exitCode == 0 ? null : ClassificarStderrYtDlp(stderr);

    /// <summary>stderr do yt-dlp → <see cref="ClasseFalhaCaption.BloqueioBot"/>, ou null (independe do exit code).</summary>
    public static FalhaCaptionClassificada? ClassificarStderrYtDlp(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return null;

        // Não compara o apóstrofo de "you're" (reto ou tipográfico).
        return stderr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
               && stderr.Contains("not a bot", StringComparison.OrdinalIgnoreCase)
            ? new FalhaCaptionClassificada(ClasseFalhaCaption.BloqueioBot, null, "sign_in_to_confirm_not_a_bot")
            : null;
    }

    private static FalhaCaptionClassificada? ClassificarGoogleApi(Google.GoogleApiException g)
    {
        var status = (int)g.HttpStatusCode;
        var reasons = Reasons(g).ToList();
        var reason = reasons.FirstOrDefault();

        if (EhQuota(g))
            return new(ClasseFalhaCaption.QuotaExcedida, status, reasons.First(r => ReasonsQuota.Contains(r)));

        if (g.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests || reasons.Any(r => ReasonsRateLimit.Contains(r)))
            return new(ClasseFalhaCaption.Transitorio, status, reason);

        if (g.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
            return new(ClasseFalhaCaption.SemPermissao, status, reason);

        if (g.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
            return new(ClasseFalhaCaption.CredencialInvalida, status, reason);

        if (status >= 500)
            return new(ClasseFalhaCaption.Transitorio, status, reason);

        return null;
    }

    private static FalhaCaptionClassificada? ClassificarToken(TokenResponseException t)
    {
        var erro = t.Error?.Error;
        if (erro is not null && ErrosOAuthCredencial.Contains(erro))
            return new(ClasseFalhaCaption.CredencialInvalida, (int?)t.StatusCode, erro);

        return t.StatusCode is >= System.Net.HttpStatusCode.InternalServerError
            ? new(ClasseFalhaCaption.Transitorio, (int)t.StatusCode, erro)
            : null;
    }

    private static IEnumerable<string> Reasons(Google.GoogleApiException g) =>
        g.Error?.Errors?.Select(e => e.Reason).Where(r => !string.IsNullOrEmpty(r)).Cast<string>() ?? [];

    private static T? ProcurarNaCadeia<T>(Exception ex) where T : Exception
    {
        for (var atual = ex; atual is not null; atual = atual.InnerException)
            if (atual is T t) return t;
        return null;
    }
}
