using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Resolve a cadeia de configs de execução (prompt + Model/MaxTokens/UsarObservabilidade) de um
/// canal pra uma etapa, já ordenada — essa ordem É o FallbackChain daquele canal (US #106).
/// Duas implementações (Repositorio/Arquivo, ver Infrastructure) escolhidas por
/// EtapaProvedorConfig:Provider; um decorator de cache (memória ou Redis) fica por cima.
/// </summary>
public interface IEtapaProvedorConfigService
{
    Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default);
}
