namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Resolve o caminho do arquivo de cookies Netscape do yt-dlp para um ChannelId.
/// Cada canal (Instituto, Ministério, …) tem cookies próprios no SSM
/// (<c>{secrets_key}:YtDlp:CookiesContent</c>), escritos em arquivo temp no boot.
/// </summary>
public interface IChannelYtDlpCookies
{
    /// <summary>Caminho do arquivo de cookies do canal, ou null se não configurado.</summary>
    string? GetCookiesFilePath(string channelId);
}

/// <summary>
/// Sem cookies por canal — fallback pro path legado em options (US #144) ou sem --cookies.
/// Registrado por padrão em <c>AddTranscriptionService</c>; substituído por
/// <c>AddYtDlpCookiesPorCanal</c> no Transcricao.Worker (US #146).
/// </summary>
public sealed class NullChannelYtDlpCookies : IChannelYtDlpCookies
{
    public string? GetCookiesFilePath(string channelId) => null;
}
