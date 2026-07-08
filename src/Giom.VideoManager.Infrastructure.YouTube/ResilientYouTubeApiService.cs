using Giom.VideoManager.Domain.Exceptions;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Giom.VideoManager.Infrastructure.YouTube;

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
            // MinimumThroughput=1 + FailureRatio=1.0: abre já na PRIMEIRA ocorrência de
            // quotaExceeded, não depois de acumular falhas numa janela — quota estourada não é
            // "problema intermitente" que melhora com mais uma tentativa, é fato consumado até
            // o reset diário (meia-noite Pacific Time). BreakDuration de 1h (não 5min): reabrir
            // em minutos só bateria em quotaExceeded de novo e reabriria o circuito à toa pelo
            // resto da run.
            FailureRatio = 1.0,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 1,
            BreakDuration = TimeSpan.FromHours(1),
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
        catch (BrokenCircuitException ex)
        {
            _logger.LogWarning("Circuit breaker aberto em {Operation} — quota provavelmente esgotada.", name);
            // Quota diária do YouTube só reseta à meia-noite Pacific Time — sem Retry-After
            // conhecido aqui, o consumer usa o delay padrão configurado (RateLimitDelaySeconds).
            throw new RateLimitExcedidoException($"YouTube.{name}", innerException: ex);
        }
        catch (Google.GoogleApiException ex) when (
            ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden &&
            ex.Error?.Errors?.Any(e => e.Reason == "quotaExceeded") == true)
        {
            _logger.LogWarning("Quota da YouTube API esgotada em {Operation}.", name);
            throw new RateLimitExcedidoException($"YouTube.{name}", innerException: ex);
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
