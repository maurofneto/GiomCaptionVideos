using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Giom.VideoManager.Infrastructure.Audio;

public static class AudioServiceCollectionExtensions
{
    /// <summary>
    /// Registra <see cref="IAudioDownloader"/> (yt-dlp) — idempotente (TryAdd), chamado por cada
    /// provedor de transcrição por áudio (Whisper, GrokVoice). Se ainda não houver
    /// <see cref="IChannelYtDlpCookies"/>, usa <see cref="NullChannelYtDlpCookies"/> (cookies reais
    /// vêm de AddYtDlpCookiesPorCanal, registrado depois e que prevalece).
    /// </summary>
    public static IServiceCollection AddYtDlpAudioDownloader(this IServiceCollection services, IConfiguration config)
    {
        services.TryAddSingleton<IChannelYtDlpCookies, NullChannelYtDlpCookies>();
        services.AddOptions<YtDlpAudioOptions>().Bind(config.GetSection("YtDlpAudio"));
        services.TryAddSingleton<IAudioDownloader, YtDlpAudioDownloader>();
        return services;
    }
}
