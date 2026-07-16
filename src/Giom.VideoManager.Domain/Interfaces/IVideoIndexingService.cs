using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Indexa um vídeo (gera embeddings + persiste chunks) — só o que EmbeddingEtapaHandler/
/// InstitutoVideoHandler precisam, separado de IBuscaSemanticaService (ISP: quem indexa nunca
/// busca, quem busca nunca indexa). Cada provedor de embedding (OpenAI/Voyage/Cohere) continua
/// implementando o IEmbeddingService combinado internamente — esta interface e
/// IBuscaSemanticaService são só as portas que a Composition expõe pra fora, sempre resolvidas
/// pra PipelineEmbeddingService (roda todos os provedores) e FallbackEmbeddingService (para no
/// primeiro que funcionar) respectivamente, sem config nem troca em runtime.
/// </summary>
public interface IVideoIndexingService
{
    Task IndexarAsync(Video video, CancellationToken ct = default);
}
