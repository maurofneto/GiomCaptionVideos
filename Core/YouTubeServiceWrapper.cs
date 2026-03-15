using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;

namespace Core
{
    public interface IYouTubeApiService
    {
        Task<PlaylistItemListResponse> ListPlaylistItemsAsync(PlaylistItemsResource.ListRequest request);
        Task<VideoListResponse> ListVideosAsync(VideosResource.ListRequest request);
        Task<CaptionListResponse> ListCaptionsAsync(CaptionsResource.ListRequest request);
        // Adicione outros métodos conforme necessário (DownloadCaption, etc.)
    }

    public class YouTubeApiService : IYouTubeApiService
    {
        private readonly YouTubeService _youtubeService;
        private readonly ILogger<YouTubeApiService> _logger;

        public YouTubeApiService(YouTubeService youtubeService, ILogger<YouTubeApiService> logger)
        {
            _youtubeService = youtubeService;
            _logger = logger;
        }

        public async Task<PlaylistItemListResponse> ListPlaylistItemsAsync(PlaylistItemsResource.ListRequest request)
        {
            return await ExecuteWithResilienceAsync(() => request.ExecuteAsync(), "ListPlaylistItems");
        }

        public async Task<VideoListResponse> ListVideosAsync(VideosResource.ListRequest request)
        {
            return await ExecuteWithResilienceAsync(() => request.ExecuteAsync(), "ListVideos");
        }

        // ... outros métodos

        private async Task<T> ExecuteWithResilienceAsync<T>(Func<Task<T>> operation, string name)
        {
            // mesma implementação do helper acima, mas usando _logger
            // ou injete IResiliencePipeline se usar Polly v8+
        }
    }
}
