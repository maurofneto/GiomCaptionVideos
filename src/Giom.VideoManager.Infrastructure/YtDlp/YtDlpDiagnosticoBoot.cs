using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.YtDlp;

/// <summary>
/// Loga no boot a versão do yt-dlp e do deno (runtime JS do EJS/n-challenge) e se PO token/proxy
/// estão ligados — pra correlacionar "Sign in to confirm you're not a bot" com a imagem que
/// estava rodando. Binário ausente ou travado só gera Warning: nunca impede o worker de subir.
/// </summary>
public sealed class YtDlpDiagnosticoBoot(IOptions<YtDlpOptions> options, ILogger<YtDlpDiagnosticoBoot> logger) : IHostedService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LogarVersaoAsync("yt-dlp", cancellationToken);
        await LogarVersaoAsync("deno", cancellationToken);
        logger.LogInformation("yt-dlp configurado: {Configuracao}", YtDlpArgumentos.DescreverParaLog(options.Value));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task LogarVersaoAsync(string executavel, CancellationToken ct)
    {
        try
        {
            var versao = await ObterVersaoAsync(executavel, ct);
            if (versao is null)
                logger.LogWarning("{Executavel} --version não respondeu em {Timeout}s", executavel, Timeout.TotalSeconds);
            else
                logger.LogInformation("{Executavel} versão: {Versao}", executavel, versao);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("{Executavel} indisponível no PATH ({Tipo})", executavel, ex.GetType().Name);
        }
    }

    /// <summary>Primeira linha não vazia do stdout, ou null em timeout.</summary>
    private static async Task<string?> ObterVersaoAsync(string executavel, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executavel,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.Start();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            await stderr;
            return (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
    }
}
