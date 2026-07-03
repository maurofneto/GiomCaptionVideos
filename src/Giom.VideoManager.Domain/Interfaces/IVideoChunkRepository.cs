using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Domain.Interfaces;

public interface IVideoChunkRepository
{
    Task DeletarPorVideoAsync(string videoId, CancellationToken ct = default);
    Task InserirAsync(IEnumerable<VideoChunk> chunks, CancellationToken ct = default);
}
