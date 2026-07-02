namespace Domain.ValueObjects;

public record ChannelConfig(
    string ChannelId,
    string? PlaylistId,
    string Name,
    bool Mine = false
);
