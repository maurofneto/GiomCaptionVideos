using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.Whisper;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Whisper via API OpenAI (download de áudio com yt-dlp + speech-to-text). Registra
    /// <see cref="ITranscricaoService"/> e o keyed <see cref="ICaptionService"/> "Whisper"
    /// pra cadeia de fallback. Se ainda não houver <see cref="IChannelYtDlpCookies"/>, usa
    /// <see cref="NullChannelYtDlpCookies"/> (cookies reais vêm de AddYtDlpCookiesPorCanal).
    /// </summary>
    public static IServiceCollection AddWhisperTranscription(this IServiceCollection services, IConfiguration config)
    {
        services.AddContextoTelemetria();
        services.AddYtDlpOpcoes(config);

        if (!services.Any(d => d.ServiceType == typeof(IChannelYtDlpCookies)))
            services.AddSingleton<IChannelYtDlpCookies, NullChannelYtDlpCookies>();

        // Timeouts bem mais folgados que os outros provedores de IA — upload de áudio (até
        // ~25MB/vídeo longo) + processamento do lado da OpenAI pode passar dos 90s padrão.
        services.AddHttpClient("whisper", client =>
        {
            client.BaseAddress = new Uri("https://api.openai.com/");
            client.Timeout = TimeSpan.FromMinutes(10);
        }).AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
            options.RegistrarEventoDeRetryNoSpan("Whisper");
        });

        services.Configure<WhisperOptions>(config.GetSection("Whisper"));
        services.AddSingleton<ITranscricaoService, WhisperTranscricaoService>();
        services.AddKeyedSingleton<ICaptionService, WhisperCaptionService>("Whisper");
        return services;
    }
}
