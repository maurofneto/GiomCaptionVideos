using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.YouTube;

/// <summary>Canal cuja credencial Google não resolveu no bootstrap (ex.: invalid_grant) + o motivo.</summary>
public sealed record FalhaCredencialCanal(CanalIngestaoConfig Canal, Exception Erro);

/// <summary>
/// Bug #147 — falha de credencial por canal é logada em ConfigureServices, antes do SentrySdk.Init,
/// e o sink descarta o evento (SentrySdk.IsEnabled ainda false): a task terminava com sucesso,
/// sem processar nada e sem nenhum incidente. IHostedLifecycleService.StartedAsync roda depois do
/// StartAsync de TODOS os hosted services (independe da ordem de registro do Sentry), então o
/// log aqui chega no Sentry. Só reporta — o exit code do processo não muda.
/// </summary>
public sealed class ReporteFalhasCredencialCanais(
    IReadOnlyList<FalhaCredencialCanal> falhas,
    int totalCanaisAtivos,
    int canaisValidos,
    ILogger<ReporteFalhasCredencialCanais> logger) : IHostedLifecycleService
{
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        foreach (var falha in falhas)
        {
            logger.LogError(falha.Erro,
                "Credencial Google do canal {CanalNome} ({ChannelId}) inválida ou expirada — canal fora desta execução",
                falha.Canal.Nome, falha.Canal.ChannelId);
        }

        if (canaisValidos == 0)
        {
            logger.LogCritical(
                "Nenhum canal com credencial Google válida ({TotalCanaisAtivos} ativos) — esta execução não processa nenhum vídeo",
                totalCanaisAtivos);
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
