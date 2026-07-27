using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>Único implementador público de ICategorizacaoService — mesmo raciocínio de FallbackResumoService (US #106), incluindo o estampo de UsarObservabilidade no ISessionContext ambiente.</summary>
public sealed class FallbackCategorizacaoService(
    IEtapaProvedorConfigService configService,
    IServiceProvider serviceProvider,
    ISessionContext sessionContext,
    ILogger<FallbackCategorizacaoService> logger) : ICategorizacaoService
{
    public async Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, CancellationToken ct = default)
    {
        var cadeia = await configService.ObterCadeiaAsync(video.ChannelId, "categorizacao", ct);

        logger.LogInformation("Cadeia de categorizacao resolvida pra canal {ChannelId}: {Count} provedor(es) [{Provedores}]",
            video.ChannelId, cadeia.Count, string.Join(",", cadeia.Select(c => $"{c.Provedor}(origem={c.Origem})")));

        var elos = cadeia
            .Select(config => (config.Provedor, (Func<CancellationToken, Task<IReadOnlyList<CategoriaTeologica>>>)(token =>
            {
                sessionContext.Definir(sessionContext.Atual.ComUsarObservabilidade(config.UsarObservabilidade));
                return serviceProvider.GetRequiredKeyedService<ICategorizacaoProvedorService>(config.Provedor).ClassificarAsync(video, config, token);
            })))
            .ToList();

        return await FallbackChain.Montar(elos).ExecutarAsync("categorizacao", logger, ct, resultadoVazio: r => r is null or { Count: 0 });
    }
}
