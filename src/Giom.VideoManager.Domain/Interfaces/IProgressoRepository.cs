namespace Giom.VideoManager.Domain.Interfaces;

public interface IProgressoRepository
{
    Task<string?> GetLastPageTokenAsync(string channelKey, string playlistId, CancellationToken ct = default);
    Task<DateTime?> GetLastProcessedDateAsync(string channelKey, string playlistId, CancellationToken ct = default);
    Task SaveProgressoAsync(string channelKey, string playlistId, string? pageToken, DateTime? maxProcessedDate, CancellationToken ct = default);
}
