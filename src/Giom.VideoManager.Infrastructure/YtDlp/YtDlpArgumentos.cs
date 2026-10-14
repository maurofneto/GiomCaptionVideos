namespace Giom.VideoManager.Infrastructure.YtDlp;

/// <summary>Argumentos/ambiente comuns a toda chamada do yt-dlp, derivados de <see cref="YtDlpOptions"/>.</summary>
public static class YtDlpArgumentos
{
    /// <summary>
    /// Com PO token desligado o plugin bgutil (sempre presente na imagem) é desativado por
    /// completo — sem isso ele tentaria o servidor default (127.0.0.1:4416) a cada vídeo.
    /// </summary>
    public const string VariavelSemPlugins = "YTDLP_NO_PLUGINS";

    public static bool PoTokenAtivo(YtDlpOptions options) =>
        options.PoToken.Habilitado && !string.IsNullOrWhiteSpace(options.PoToken.ProviderUrl);

    public static bool ProxyAtivo(YtDlpOptions options) => !string.IsNullOrWhiteSpace(options.Proxy);

    /// <summary>Prefixo de argumentos (antes dos específicos de cada chamada) — um item por argumento, sem aspas.</summary>
    public static List<string> Comuns(YtDlpOptions options)
    {
        var argumentos = new List<string>();

        if (ProxyAtivo(options))
            argumentos.AddRange(["--proxy", options.Proxy.Trim()]);

        if (PoTokenAtivo(options))
            argumentos.AddRange(["--extractor-args", $"youtubepot-bgutilhttp:base_url={options.PoToken.ProviderUrl.Trim()}"]);

        return argumentos;
    }

    /// <summary>Variáveis de ambiente do processo yt-dlp.</summary>
    public static Dictionary<string, string> Ambiente(YtDlpOptions options) =>
        PoTokenAtivo(options) ? [] : new() { [VariavelSemPlugins] = "1" };

    /// <summary>Resumo seguro pra log: só liga/desliga, nunca a URL do proxy.</summary>
    public static string DescreverParaLog(YtDlpOptions options) =>
        $"PoToken={(PoTokenAtivo(options) ? "on" : "off")} Proxy={(ProxyAtivo(options) ? "on" : "off")}";

    /// <summary>
    /// Remove o proxy (inteiro e a parte usuario:senha) de uma linha de saída do yt-dlp antes de
    /// re-emitir/capturar — mensagens de erro de rede podem ecoar a URL do proxy.
    /// </summary>
    public static string MascararSegredos(string linha, YtDlpOptions options)
    {
        if (!ProxyAtivo(options) || string.IsNullOrEmpty(linha)) return linha;

        var proxy = options.Proxy.Trim();
        var mascarada = linha.Replace(proxy, "***", StringComparison.Ordinal);

        if (Uri.TryCreate(proxy, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            mascarada = mascarada.Replace(uri.UserInfo, "***", StringComparison.Ordinal);

        return mascarada;
    }
}
