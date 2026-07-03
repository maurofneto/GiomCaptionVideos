using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.Whisper;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWhisperTranscription(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<WhisperOptions>(config.GetSection("Whisper"));
        services.AddSingleton<ITranscricaoService, WhisperTranscricaoService>();
        return services;
    }
}
