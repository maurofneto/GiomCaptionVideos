using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>Leitura da tabela etapa_provedor_config (US #106) — implementação HTTP em Infrastructure.Supabase.</summary>
public interface IEtapaProvedorConfigRepository
{
    Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default);
}
