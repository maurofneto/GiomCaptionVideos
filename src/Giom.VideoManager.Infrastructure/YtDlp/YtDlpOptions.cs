namespace Giom.VideoManager.Infrastructure.YtDlp;

/// <summary>
/// Seção <c>YtDlp</c> — opções comuns a toda invocação do yt-dlp (YtDlpCaption e download de
/// áudio do Whisper). Tudo desligado por padrão: o comportamento só muda quando o SSM
/// (<c>/giom/{env}/pipeline/YtDlp/...</c>) preenche os valores.
/// </summary>
public class YtDlpOptions
{
    public YtDlpPoTokenOptions PoToken { get; set; } = new();

    /// <summary>
    /// URL de proxy repassada em <c>--proxy</c> (ex.: <c>http://usuario:senha@host:porta</c>).
    /// Vazio = sem proxy. Pode conter credencial — nunca logar o valor, só se está ligado.
    /// </summary>
    public string Proxy { get; set; } = string.Empty;
}

/// <summary>
/// PO token via plugin bgutil-ytdlp-pot-provider (instalado na imagem do Transcricao.Worker). O
/// servidor HTTP do provider roda fora do processo (sidecar/serviço) — aqui só apontamos pra ele.
/// </summary>
public class YtDlpPoTokenOptions
{
    public bool Habilitado { get; set; }

    /// <summary>Base URL do servidor bgutil (ex.: <c>http://127.0.0.1:4416</c>). Vazio = desligado, mesmo com <see cref="Habilitado"/>.</summary>
    public string ProviderUrl { get; set; } = string.Empty;
}
