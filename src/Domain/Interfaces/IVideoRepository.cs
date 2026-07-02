using Domain.Entities;

namespace Domain.Interfaces;

public interface IVideoRepository
{
    Task<Video?> GetByIdAsync(string videoId, CancellationToken ct = default);
    Task SaveAsync(Video video, CancellationToken ct = default);
    Task UpdateAsync(Video video, CancellationToken ct = default);
}
