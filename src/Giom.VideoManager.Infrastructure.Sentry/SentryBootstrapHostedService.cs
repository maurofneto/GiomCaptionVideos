using Giom.VideoManager.Infrastructure.Observabilidade;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Giom.VideoManager.Infrastructure.Sentry;

/// <summary>
/// Amarra o ciclo de vida do TracerProvider/SentrySdk (ver SentryBootstrap) ao ciclo de vida do
/// host — StartAsync inicializa (ordem garantida antes de qualquer Activity ser criada pelo
/// resto da aplicação, já que hosted services rodam antes do código principal), StopAsync
/// descarta (força o flush de spans/eventos pendentes antes do processo encerrar). Registrar
/// isso como AddSingleton(instância) não funcionava — o container nunca descarta instâncias que
/// não foram criadas por ele mesmo; um IHostedService resolve isso porque o host sempre chama
/// StopAsync nele. Recebe as regras/scrubbers de ISentryTransactionExclusionRule/
/// ISentryDataScrubber via DI (Bug #128) — resolvidas aqui (não em SentryBootstrap.Initialize
/// direto) porque hosted services só rodam depois do IServiceProvider pronto.
/// </summary>
internal sealed class SentryBootstrapHostedService(
    IConfiguration config,
    IEnumerable<ISentryTransactionExclusionRule> regrasDeExclusao,
    IEnumerable<ISentryDataScrubber> scrubbers) : IHostedService
{
    private IDisposable? _recursos;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _recursos = SentryBootstrap.Initialize(config, regrasDeExclusao, scrubbers);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _recursos?.Dispose();
        return Task.CompletedTask;
    }
}
