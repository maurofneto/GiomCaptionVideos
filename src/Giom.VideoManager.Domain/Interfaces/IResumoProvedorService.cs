using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Gera resumo usando UM provedor específico (Claude/Grok/OpenAI), com a config resolvida pro
/// canal daquele vídeo (US #106) — não decide fallback, isso é responsabilidade de
/// FallbackResumoService (único implementador de IResumoService, o contrato público). Separado
/// de IResumoService pra não vazar EtapaProvedorConfig (detalhe de resolução por canal) pro
/// consumidor externo (ResumoEtapaHandler), que só quer "gerar o resumo desse vídeo".
/// </summary>
public interface IResumoProvedorService
{
    Task<Resumo?> GerarResumoAsync(Video video, EtapaProvedorConfig config, CancellationToken ct = default);
}
