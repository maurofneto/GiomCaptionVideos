namespace Giom.VideoManager.Infrastructure.YouTube;

/// <summary>
/// Causa classificada de uma falha ao obter legenda (YouTubeCaption / YtDlpCaption). Só
/// classifica e loga (PR-02) — a decisão de retry/reagendamento por classe é da Application (PR-08).
/// </summary>
public enum ClasseFalhaCaption
{
    /// <summary><c>captions.list</c> voltou sem nenhuma faixa utilizável.</summary>
    SemLegendaAinda,

    /// <summary>403 que não é quota (<c>forbidden</c>/<c>insufficientPermissions</c>/...): a conta não tem permissão no canal.</summary>
    SemPermissao,

    /// <summary>403 com reason <c>quotaExceeded</c>/<c>dailyLimitExceeded</c>, ou circuito de quota aberto.</summary>
    QuotaExcedida,

    /// <summary>OAuth recusado (<c>invalid_grant</c>/<c>invalid_client</c>), 401, ou credencial do canal indisponível.</summary>
    CredencialInvalida,

    /// <summary>yt-dlp bloqueado pelo YouTube ("Sign in to confirm you're not a bot").</summary>
    BloqueioBot,

    /// <summary>200 com corpo realmente vazio.</summary>
    LegendaVaziaReal,

    /// <summary>5xx, timeout/erro de rede, 429/<c>rateLimitExceeded</c>.</summary>
    Transitorio
}
