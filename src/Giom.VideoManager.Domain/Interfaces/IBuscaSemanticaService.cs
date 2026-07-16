namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Busca semântica (RAG) — só o que ChatBotProcessor precisa, separado de
/// IVideoIndexingService (ISP: ver comentário lá). Sempre resolvida pra
/// FallbackEmbeddingService na Composition — tenta os provedores em ordem, para no primeiro
/// que funcionar.
/// </summary>
public interface IBuscaSemanticaService
{
    /// <param name="canalId">Restringe a busca a um único canal do YouTube (ex.: para o bot do Instituto nunca retornar conteúdo do Ministério); null = sem restrição.</param>
    Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default);
}
