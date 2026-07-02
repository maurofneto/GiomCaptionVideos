using Domain.ValueObjects;

namespace Domain.Interfaces;

/// <summary>
/// Obtém a playlist de vídeos de uma fonte (YouTube, etc.) com suporte a paginação.
/// </summary>
public interface IVideoSourceService
{
    Task<string> GetUploadPlaylistIdAsync(ChannelConfig config, CancellationToken ct = default);
    Task<PlaylistPage> GetPlaylistPageAsync(string playlistId, int maxResults, string? pageToken, CancellationToken ct = default);
}
