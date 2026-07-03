using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.Logging;
using System.Xml;

namespace Giom.VideoManager.Infrastructure.YouTube;

/// <summary>
/// Implementação de IVideoSourceService usando a YouTube Data API v3.
/// </summary>
public class YouTubeVideoSourceService : IVideoSourceService
{
    private readonly ResilientYouTubeApiService _api;
    private readonly YouTubeService _youtube;
    private readonly ILogger<YouTubeVideoSourceService> _logger;

    public YouTubeVideoSourceService(YouTubeService youtube, ILogger<YouTubeVideoSourceService> logger)
    {
        _youtube = youtube;
        _logger = logger;
        _api = new ResilientYouTubeApiService(youtube, Microsoft.Extensions.Logging.Abstractions.NullLogger<ResilientYouTubeApiService>.Instance);
    }

    public async Task<string> GetUploadPlaylistIdAsync(ChannelConfig config, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(config.PlaylistId))
            return config.PlaylistId;

        var request = _youtube.Channels.List("contentDetails");
        if (config.Mine)
            request.Mine = true;
        else
            request.Id = config.ChannelId;

        var response = await _api.ListChannelsAsync(request);
        return response.Items?.FirstOrDefault()?.ContentDetails?.RelatedPlaylists?.Uploads ?? string.Empty;
    }

    public async Task<PlaylistPage> GetPlaylistPageAsync(string playlistId, int maxResults, string? pageToken, CancellationToken ct = default)
    {
        var request = _youtube.PlaylistItems.List("contentDetails,snippet,status");
        request.PlaylistId = playlistId;
        request.MaxResults = maxResults;
        if (!string.IsNullOrEmpty(pageToken))
            request.PageToken = pageToken;

        var response = await _api.ListPlaylistItemsAsync(request);

        if (response.Items == null || response.Items.Count == 0)
            return new PlaylistPage([], null);

        var videoIds = response.Items
            .Select(i => i.Snippet?.ResourceId?.VideoId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        var detailsRequest = _youtube.Videos.List("contentDetails,snippet,status,liveStreamingDetails");
        detailsRequest.Id = string.Join(",", videoIds);
        var detailsResponse = await _api.ListVideosAsync(detailsRequest);
        var details = detailsResponse.Items ?? [];

        var videos = response.Items
            .Select(item => MapToVideo(item, details))
            .ToList();

        return new PlaylistPage(videos, response.NextPageToken);
    }

    private static Video MapToVideo(
        Google.Apis.YouTube.v3.Data.PlaylistItem item,
        IList<Google.Apis.YouTube.v3.Data.Video> details)
    {
        var detail = details.FirstOrDefault(v => v.Id == item.Snippet?.ResourceId?.VideoId);

        return new Video
        {
            Id = item.Snippet?.ResourceId?.VideoId ?? "",
            PlaylistId = item.Snippet?.PlaylistId ?? "",
            ChannelId = item.Snippet?.ChannelId ?? "",
            ChannelTitle = item.Snippet?.ChannelTitle ?? "",
            Title = item.Snippet?.Title ?? "",
            Description = item.Snippet?.Description ?? "",
            Length = detail?.ContentDetails?.Duration is not null
                ? XmlConvert.ToTimeSpan(detail.ContentDetails.Duration).ToString()
                : "",
            PrivacyStatus = item.Status?.PrivacyStatus ?? "",
            UploadedAt = item.Snippet?.PublishedAtDateTimeOffset?.LocalDateTime,
            Type = DetectType(detail)
        };
    }

    private static VideoType DetectType(Google.Apis.YouTube.v3.Data.Video? detail)
    {
        if (detail == null) return VideoType.Upload;

        // Live: tem liveStreamingDetails com actualStartTime (transmissão já ocorrida)
        if (detail.LiveStreamingDetails?.ActualStartTimeDateTimeOffset != null)
            return VideoType.Live;

        // Premiere: agendado como estreia (actualStartTime ausente mas scheduledStartTime presente)
        if (detail.LiveStreamingDetails?.ScheduledStartTimeDateTimeOffset != null)
            return VideoType.Premiere;

        // Short: duração <= 60 segundos
        if (detail.ContentDetails?.Duration is not null)
        {
            var duration = XmlConvert.ToTimeSpan(detail.ContentDetails.Duration);
            if (duration.TotalSeconds <= 60)
                return VideoType.Short;
        }

        return VideoType.Upload;
    }
}
