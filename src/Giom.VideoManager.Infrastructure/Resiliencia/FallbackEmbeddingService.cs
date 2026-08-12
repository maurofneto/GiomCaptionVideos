using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Encadeia N providers de IEmbeddingService pra busca semântica — tenta em ordem, para no
/// primeiro que funcionar. Resolve a cadeia do CANAL via IEtapaProvedorConfigService (US #129,
/// mesmo mecanismo de FallbackResumoService/US #106) — <paramref name="canalId"/> é obrigatório
/// na prática (ChatBotProcessor sempre passa RagOptions.CanalId; não há canal "default" pra
/// resolver a config). Único implementador de IBuscaSemanticaService (ver Feature #115) —
/// indexação usa PipelineEmbeddingService (roda todos), interfaces separadas (ISP) pra
/// ChatBotProcessor nunca precisar saber que "indexar" existe.
/// </summary>
public sealed class FallbackEmbeddingService(
    IEtapaProvedorConfigService configService,
    IServiceProvider serviceProvider,
    ILogger<FallbackEmbeddingService> logger) : IBuscaSemanticaService
{
    public async Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(canalId))
            throw new InvalidOperationException("Busca semântica por embedding exige canalId (US #129) — a cadeia de provedores agora é resolvida por canal, não há mais uma cadeia global fixa.");

        var cadeia = await configService.ObterCadeiaAsync(canalId, "embedding", ct);

        var elos = cadeia
            .Select(config => (config.Provedor, (Func<CancellationToken, Task<BuscaSemanticaResultado>>)(token =>
                serviceProvider.GetRequiredKeyedService<IEmbeddingService>(config.Provedor).BuscarAsync(pergunta, area, canalId, limite, threshold, token))))
            .ToList();

        return await FallbackChain.Montar(elos).ExecutarAsync("embedding_buscar", logger, ct);
    }
}
