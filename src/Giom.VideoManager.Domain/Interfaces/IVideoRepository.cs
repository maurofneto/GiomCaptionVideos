using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Domain.Interfaces;

public interface IVideoRepository
{
    Task<Video?> GetByIdAsync(string videoId, CancellationToken ct = default);
    Task UpsertAsync(Video video, CancellationToken ct = default);
}
