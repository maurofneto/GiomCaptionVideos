using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Infrastructure.YouTube;

/// <summary>
/// Wrapper do YouTubeService com Polly — retry exponencial + circuit breaker para quota.
/// </summary>
internal class ResilientYouTubeApiService
{
    private readonly YouTubeService _youtube;
    private readonly ILogger<ResilientYouTubeApiService> _logger;
    private readonly ResiliencePipeline _pipeline;

    public ResilientYouTubeApiService(YouTubeService youtube, ILogger<ResilientYouTubeApiService> logger)
    {
        _youtube = youtube;
        _logger = logger;

        var retry = new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(2),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder().Handle<Google.GoogleApiException>(ex =>
                ex.HttpStatusCode >= System.Net.HttpStatusCode.InternalServerError)
        };

        var circuitBreaker = new CircuitBreakerStrategyOptions
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
            .AddRetry(retry)
            .AddCircuitBreaker(circuitBreaker)
            .Build();
    }

    public Task<PlaylistItemListResponse> ListPlaylistItemsAsync(PlaylistItemsResource.ListRequest request)
        => Execute(request.ExecuteAsync, nameof(ListPlaylistItemsAsync));

    public Task<VideoListResponse> ListVideosAsync(VideosResource.ListRequest request)
        => Execute(request.ExecuteAsync, nameof(ListVideosAsync));

    public Task<CaptionListResponse> ListCaptionsAsync(CaptionsResource.ListRequest request)
        => Execute(request.ExecuteAsync, nameof(ListCaptionsAsync));

    public Task<ChannelListResponse> ListChannelsAsync(ChannelsResource.ListRequest request)
        => Execute(request.ExecuteAsync, nameof(ListChannelsAsync));

    public Task DownloadCaptionAsync(CaptionsResource.DownloadRequest request, MemoryStream destination)
        => ExecuteVoid(() => request.DownloadAsync(destination), nameof(DownloadCaptionAsync));

    private async Task<T> Execute<T>(Func<Task<T>> operation, string name)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async _ => await operation());
        }
        catch (BrokenCircuitException)
        {
            _logger.LogWarning("Circuit breaker aberto em {Operation} — quota provavelmente esgotada.", name);
            throw;
        }
        catch (Google.GoogleApiException ex) when (
            ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden &&
            ex.Error?.Errors?.Any(e => e.Reason == "quotaExceeded") == true)
        {
            _logger.LogWarning("Quota da YouTube API esgotada em {Operation}.", name);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha em {Operation}", name);
            throw;
        }
    }

    private async Task ExecuteVoid(Func<Task> operation, string name)
    {
        await Execute(async () => { await operation(); return true; }, name);
    }
}
