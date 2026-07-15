using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Infrastructure.Observabilidade;

/// <summary>
/// Implementação de IEtapaProvedorConfigService que lê do banco (IEtapaProvedorConfigRepository,
/// concreto em Infrastructure.Supabase) — US #106. Só repassa; a lógica de origem/mapeamento já
/// mora no repositório (Origem="Repository" setado lá). Escolhida via
/// EtapaProvedorConfig:Provider=Repository (default).
/// </summary>
public sealed class RepositorioEtapaProvedorConfigService(IEtapaProvedorConfigRepository repository) : IEtapaProvedorConfigService
{
    public Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default) =>
        repository.ObterCadeiaAsync(channelId, etapa, ct);
}
