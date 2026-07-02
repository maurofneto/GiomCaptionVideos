using Domain.Entities;

namespace Domain.Interfaces;

public interface IVideoChunkRepository
{
    Task DeletarPorVideoAsync(string videoId, CancellationToken ct = default);
    Task InserirAsync(IEnumerable<VideoChunk> chunks, CancellationToken ct = default);
}
