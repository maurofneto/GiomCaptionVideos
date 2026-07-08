using Giom.VideoManager.Infrastructure.YouTube;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.YouTube;

public class ResilientYouTubeApiServiceTests
{
    /// <summary>
    /// Constrói o pipeline Polly de verdade (retry + circuit breaker) — não só os objetos de
    /// options isolados. Polly valida CircuitBreakerStrategyOptions em runtime dentro de
    /// AddCircuitBreaker (ValidationException se MinimumThroughput &lt; 2, por exemplo) — um teste
    /// que só constrói o record de options nunca aciona essa validação e passaria mesmo com
    /// configuração inválida (foi exatamente o que aconteceu: MinimumThroughput=1 só quebrou
    /// quando o worker rodou de verdade, não em nenhum teste).
    /// </summary>
    [Fact]
    public void Constructor_MontaOPipelinePollySemLancar()
    {
        using var youtube = new YouTubeService(new BaseClientService.Initializer { ApiKey = "fake-key-sem-chamada-real" });

        var exception = Record.Exception(() => new ResilientYouTubeApiService(youtube, NullLogger<ResilientYouTubeApiService>.Instance));

        Assert.Null(exception);
    }
}
