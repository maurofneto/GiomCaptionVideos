namespace Domain.ValueObjects;

/// <summary>Página de resultados de playlist retornada pelo IVideoSourceService.</summary>
public record PlaylistPage(
    IReadOnlyList<Entities.Video> Videos,
    string? NextPageToken
);
