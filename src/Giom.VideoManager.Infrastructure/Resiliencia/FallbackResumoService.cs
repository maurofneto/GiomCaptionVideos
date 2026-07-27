using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Único implementador público de IResumoService — resolve a cadeia de fallback do CANAL do
/// vídeo via IEtapaProvedorConfigService (US #106; cada linha já tem Provedor + Ordem + a
/// config daquele provedor) e encadeia via FallbackChain, igual sempre foi. Os provedores
/// concretos (ClaudeResumoService etc.) implementam IResumoProvedorService, resolvidos aqui
/// Keyed por Provedor — não dependem mais de uma cadeia fixa registrada no DI (ver Composition).
/// Estampa config.UsarObservabilidade no ISessionContext ambiente antes de cada tentativa — é o
/// único lugar que sabe essa decisão veio de qual config; o provedor concreto nunca lê o campo.
/// </summary>
public sealed class FallbackResumoService(
    IEtapaProvedorConfigService configService,
    IServiceProvider serviceProvider,
    ISessionContext sessionContext,
    ILogger<FallbackResumoService> logger) : IResumoService
{
    public async Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
    {
        var cadeia = await configService.ObterCadeiaAsync(video.ChannelId, "resumo", ct);

        var elos = cadeia
            .Select(config => (config.Provedor, (Func<CancellationToken, Task<Resumo?>>)(token =>
            {
                sessionContext.Definir(sessionContext.Atual.ComUsarObservabilidade(config.UsarObservabilidade));
                return serviceProvider.GetRequiredKeyedService<IResumoProvedorService>(config.Provedor).GerarResumoAsync(video, config, token);
            })))
            .ToList();

        return await FallbackChain.Montar(elos).ExecutarAsync("resumo", logger, ct);
    }
}
