using Domain.Entities;

namespace Domain.Interfaces;

public interface IVideoRepository
{
    Task<Video?> GetByIdAsync(string videoId, CancellationToken ct = default);
    Task UpsertAsync(Video video, CancellationToken ct = default);
}
