using System.Diagnostics;
using System.Text;

namespace Giom.VideoManager.Infrastructure.YtDlp;

/// <summary>Executa o yt-dlp como processo externo, já com os argumentos/ambiente comuns de <see cref="YtDlpOptions"/>.</summary>
public static class YtDlpProcesso
{
    private const int StderrMaxChars = 64 * 1024;

    /// <summary>
    /// <paramref name="argumentos"/> são os específicos da chamada; os comuns (proxy, PO token)
    /// entram antes. stderr é capturado (pra classificar bloqueio) e re-emitido linha a linha em
    /// Console.Error — o metric filter do CloudWatch ("Sign in to confirm") continua enxergando o
    /// texto original, só com o proxy mascarado.
    /// </summary>
    public static async Task<(int ExitCode, string Stderr)> ExecutarAsync(
        string executavel, IEnumerable<string> argumentos, YtDlpOptions options, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executavel,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argumento in YtDlpArgumentos.Comuns(options).Concat(argumentos))
            startInfo.ArgumentList.Add(argumento);

        foreach (var (nome, valor) in YtDlpArgumentos.Ambiente(options))
            startInfo.Environment[nome] = valor;

        using var process = new Process { StartInfo = startInfo };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var linha = YtDlpArgumentos.MascararSegredos(e.Data, options);
            Console.Error.WriteLine(linha);
            lock (stderr)
                if (stderr.Length < StderrMaxChars) stderr.AppendLine(linha);
        };

        process.Start();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        // WaitForExitAsync com stderr redirecionado já espera o EOF dos handlers de saída assíncrona.
        lock (stderr) return (process.ExitCode, stderr.ToString());
    }
}
