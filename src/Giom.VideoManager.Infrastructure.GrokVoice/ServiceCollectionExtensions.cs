using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Audio;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.GrokVoice;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Grok Voice (xAI speech-to-text) como elo da cadeia de Transcrição (US #148): registra o
    /// <see cref="ICaptionService"/> keyed "GrokVoice" (o nome que vai em
    /// etapa_provedor_config.provedor). Só entra na cadeia de um canal se houver linha dele
    /// em etapa_provedor_config — registrar aqui não muda o comportamento de ninguém.
    /// </summary>
    public static IServiceCollection AddGrokVoiceTranscription(this IServiceCollection services, IConfiguration config)
    {
        services.AddContextoTelemetria();
        services.AddYtDlpAudioDownloader(config);

        var section = config.GetSection("GrokVoice");
        var baseUrl = section["BaseUrl"] is { Length: > 0 } configurada ? configurada : new GrokVoiceOptions().BaseUrl;

        // Mesmo critério de timeout do Whisper: upload de áudio longo + processamento do lado do provedor.
        services.AddHttpClient("grokvoice", client =>
        {
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromMinutes(10);
        }).AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
            options.RegistrarEventoDeRetryNoSpan("GrokVoice");
        });

        services.Configure<GrokVoiceOptions>(section);
        services.AddKeyedSingleton<ICaptionService, GrokVoiceCaptionService>("GrokVoice");
        return services;
    }
}
