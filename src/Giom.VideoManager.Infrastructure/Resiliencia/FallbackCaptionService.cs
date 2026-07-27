using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>Encadeia N providers de ICaptionService (ex: YouTubeCaption → YtDlpCaption → Whisper) via FallbackChain.</summary>
public sealed class FallbackCaptionService(
    IReadOnlyList<(string Nome, ICaptionService Servico)> cadeia,
    ILogger<FallbackCaptionService> logger) : ICaptionService
{
    public Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        var elos = cadeia
            .Select(c => (c.Nome, (Func<CancellationToken, Task<Transcricao?>>)(token => c.Servico.GetCaptionAsync(video, token))))
            .ToList();

        return FallbackChain.Montar(elos).ExecutarAsync("transcricao", logger, ct);
    }
}
