using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>Leitura da tabela etapa_topologia (Config First) — implementação HTTP em Infrastructure.Supabase.</summary>
public interface ITopologiaRepository
{
    /// <summary>Topologia do canal, ou null se o canal não tem nenhuma linha em etapa_topologia.</summary>
    Task<TopologiaCanal?> ObterAsync(string channelId, CancellationToken ct = default);
}
