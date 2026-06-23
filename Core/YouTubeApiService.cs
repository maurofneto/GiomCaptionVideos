using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Core
{
    public interface IYouTubeApiService
    {
        Task<PlaylistItemListResponse> ListPlaylistItemsAsync(PlaylistItemsResource.ListRequest request);
        Task<VideoListResponse> ListVideosAsync(VideosResource.ListRequest request);
        Task<CaptionListResponse> ListCaptionsAsync(CaptionsResource.ListRequest request);
        Task<ChannelListResponse> ListChannelsAsync(ChannelsResource.ListRequest request);
        Task DownloadCaptionAsync(CaptionsResource.DownloadRequest downloadRequest, MemoryStream memory);
    }

    public class YouTubeApiService : IYouTubeApiService
    {
        private readonly YouTubeService _youtubeService;
        private readonly ILogger<YouTubeApiService> _logger;
        private readonly ResiliencePipeline _pipeline;

        public YouTubeApiService(YouTubeService youtubeService, ILogger<YouTubeApiService> logger)
        {
            _youtubeService = youtubeService;
            _logger = logger;

            // Polly v8: Pipeline com Retry + CircuitBreaker (avançado)
            var retryStrategy = new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Google.GoogleApiException>(ex =>
                    ex.HttpStatusCode >= System.Net.HttpStatusCode.InternalServerError)
            };

            var circuitBreakerStrategy = new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromMinutes(5),
                ShouldHandle = new PredicateBuilder().Handle<Google.GoogleApiException>(ex =>
                    ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden &&
                    ex.Error?.Errors?.Any(e => e.Reason == "quotaExceeded") == true)
            };

            _pipeline = new ResiliencePipelineBuilder()
                .AddRetry(retryStrategy)
                .AddCircuitBreaker(circuitBreakerStrategy)
                .Build();
        }

        public async Task<PlaylistItemListResponse> ListPlaylistItemsAsync(PlaylistItemsResource.ListRequest request)
        {
            return await ExecuteWithResilienceAsync(request.ExecuteAsync, "ListPlaylistItems");
        }

        public async Task<VideoListResponse> ListVideosAsync(VideosResource.ListRequest request)
        {
            return await ExecuteWithResilienceAsync(request.ExecuteAsync, "ListVideos");
        }

        public async Task<CaptionListResponse> ListCaptionsAsync(CaptionsResource.ListRequest request)
        {
            return await ExecuteWithResilienceAsync(request.ExecuteAsync, "ListCaptions");
        }

        public async Task<ChannelListResponse> ListChannelsAsync(ChannelsResource.ListRequest request)
        {
            return await ExecuteWithResilienceAsync(request.ExecuteAsync, "ListChannels");
        }

        public Task DownloadCaptionAsync(CaptionsResource.DownloadRequest downloadRequest, MemoryStream memory)
        {
            return ExecuteWithResilienceAsync(() => downloadRequest.DownloadAsync(memory), "DownloadCaption");
        }

        private async Task<T> ExecuteWithResilienceAsync<T>(Func<Task<T>> operation, string operationName)
        {
            try
            {
                return await _pipeline.ExecuteAsync(async token =>
                {
                    try
                    {
                        return await operation();
                    }
                    catch (BrokenCircuitException)
                    {
                        _logger.LogWarning("Circuit breaker aberto para {Operation}. Pulando.", operationName);
                        throw;
                    }
                });
            }
            catch (Google.GoogleApiException ex) when (
                ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden &&
                ex.Error?.Errors?.Any(e => e.Reason == "quotaExceeded") == true)
            {
                _logger.LogWarning("Quota excedida para YouTube API em {Operation}. Parando processamento.", operationName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha após resiliência em {Operation}", operationName);
                throw;
            }
        }
    }
}
