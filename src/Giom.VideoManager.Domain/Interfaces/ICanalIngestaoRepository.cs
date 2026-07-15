using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>Fonte dos canais de ingestão ativos — ver US #105. Implementado no Supabase (tabela canal_ingestao), consumido no bootstrap do Ingestao.Worker.</summary>
public interface ICanalIngestaoRepository
{
    Task<IReadOnlyList<CanalIngestaoConfig>> GetCanaisAtivosAsync(CancellationToken ct = default);
}
