using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>Classifica usando UM provedor específico, com a config resolvida pro canal (US #106) — ver IResumoProvedorService pro raciocínio de separação de ICategorizacaoService.</summary>
public interface ICategorizacaoProvedorService
{
    Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, EtapaProvedorConfig config, CancellationToken ct = default);
}
