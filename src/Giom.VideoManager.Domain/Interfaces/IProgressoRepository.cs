namespace Giom.VideoManager.Domain.Interfaces;

public interface IProgressoRepository
{
    Task<string?> GetLastPageTokenAsync(string channelKey, string playlistId, CancellationToken ct = default);
    Task<DateTime?> GetLastProcessedDateAsync(string channelKey, string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Índice do primeiro vídeo AINDA NÃO avaliado dentro da página atual (Bug #125/#126) — 0
    /// quando a página é nova. Sem isso, ao parar no meio de uma página (teto de
    /// MaxVideosPerRun batido antes de terminar), a única forma de "retomar" seria refazer a
    /// mesma página do zero, o que reconsome a cota do teto reavaliando vídeos já tratados
    /// (o handler os ignora por dedup, mas a cota da execução é gasta mesmo assim) — em vez de
    /// continuar exatamente de onde parou.
    /// </summary>
    Task<int> GetPaginaOffsetAsync(string channelKey, string playlistId, CancellationToken ct = default);

    Task SaveProgressoAsync(string channelKey, string playlistId, string? pageToken, DateTime? maxProcessedDate, int paginaOffset, CancellationToken ct = default);
}
