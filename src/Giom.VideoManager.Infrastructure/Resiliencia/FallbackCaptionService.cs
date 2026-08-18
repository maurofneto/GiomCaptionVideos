using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Único implementador público de ICaptionService — resolve a cadeia de fallback do CANAL do
/// vídeo via IEtapaProvedorConfigService (US #129, mesmo mecanismo de FallbackResumoService/US
/// #106) e encadeia via FallbackChain. Os provedores concretos (YouTubeCaptionService,
/// YouTubeTranscriptService, YtDlpCaptionService, WhisperCaptionService) são resolvidos aqui
/// Keyed por Provedor — não dependem mais de uma cadeia fixa (Transcricao:FallbackChain)
/// registrada no DI no boot (ver Composition).
/// </summary>
public sealed class FallbackCaptionService(
    IEtapaProvedorConfigService configService,
    IServiceProvider serviceProvider,
    ILogger<FallbackCaptionService> logger) : ICaptionService
{
    public async Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        var cadeia = await configService.ObterCadeiaAsync(video.ChannelId, "transcricao", ct);

        var elos = cadeia
            .Select(config => (config.Provedor, (Func<CancellationToken, Task<Transcricao?>>)(token =>
                serviceProvider.GetRequiredKeyedService<ICaptionService>(config.Provedor).GetCaptionAsync(video, token))))
            .ToList();

        return await FallbackChain.Montar(elos).ExecutarAsync("transcricao", logger, ct);
    }
}
