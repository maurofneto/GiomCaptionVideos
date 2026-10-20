namespace Giom.VideoManager.Infrastructure.YtDlp;

/// <summary>
/// Detecta no stderr do yt-dlp o bloqueio anti-bot do YouTube. Puro (sem I/O). Compartilhado
/// entre YtDlpCaption (Infrastructure.YouTube) e o download de áudio do Whisper.
/// </summary>
public static class YtDlpBloqueioBot
{
    public const string ReasonSignInNotABot = "sign_in_to_confirm_not_a_bot";
    public const string ReasonTrafegoIncomum = "unusual_traffic";

    /// <summary>Reason do bloqueio, ou null se o texto não indica bloqueio anti-bot.</summary>
    public static string? Detectar(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return null;

        // Não compara o apóstrofo de "you're" (reto ou tipográfico).
        if (stderr.Contains("confirm you", StringComparison.OrdinalIgnoreCase)
            && stderr.Contains("not a bot", StringComparison.OrdinalIgnoreCase))
            return ReasonSignInNotABot;

        // Página de "unusual traffic from your computer network" (captcha do Google).
        if (stderr.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase))
            return ReasonTrafegoIncomum;

        return null;
    }

    public static bool EhReasonDeBloqueio(string? reason) =>
        reason is ReasonSignInNotABot or ReasonTrafegoIncomum;
}
