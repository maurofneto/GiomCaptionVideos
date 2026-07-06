using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Telemetria;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.YouTube;

public class GoogleCredentialOptions
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = "Giom Video Processor";
    public string User { get; init; } = "user";

    /// <summary>Presente só em produção (ex: ECS Fargate) — habilita o fluxo não-interativo.</summary>
    public string? RefreshToken { get; init; }
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o YouTubeService autenticado com OAuth do usuário e o IVideoSourceService
    /// usado para descobrir vídeos novos nos canais monitorados.
    /// </summary>
    public static IServiceCollection AddYouTubeVideoSource(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<GoogleCredentialOptions>(config.GetSection("Google"));

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<GoogleCredentialOptions>>().Value;
            return GetGoogleCredential(options).GetAwaiter().GetResult();
        });

        services.AddSingleton(provider =>
        {
            var cred = provider.GetRequiredService<UserCredential>();
            var options = provider.GetRequiredService<IOptions<GoogleCredentialOptions>>().Value;
            return new YouTubeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = cred,
                ApplicationName = options.ApplicationName
            });
        });

        services.AddSingleton<IVideoSourceService, YouTubeVideoSourceService>();
        return services;
    }

    /// <summary>
    /// Registra ICaptionService conforme Transcription:Provider, decorado com telemetria
    /// (tempo/sucesso — nenhum desses providers cobra por chamada, então nunca reporta custo).
    /// Valores aceitos: YouTubeCaption, YtDlpCaption, YouTubeTranscript (padrão — sem quota, sem processo externo).
    /// </summary>
    public static IServiceCollection AddYouTubeCaptionService(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Transcription:Provider"] ?? "YouTubeTranscript";

        switch (provider)
        {
            case "YtDlpCaption":
                services.Configure<YtDlpCaptionOptions>(config.GetSection("YtDlpCaption"));
                services.AddSingleton<YtDlpCaptionService>();
                services.AddSingleton<ICaptionService>(sp => new TelemetriaCaptionService(
                    sp.GetRequiredService<YtDlpCaptionService>(),
                    sp.GetRequiredService<ITelemetriaService>(),
                    sp.GetRequiredService<ILogger<TelemetriaCaptionService>>()));
                break;

            case "YouTubeCaption":
                services.Configure<YouTubeCaptionOptions>(config.GetSection("YouTubeCaption"));
                services.AddSingleton<YouTubeCaptionService>();
                services.AddSingleton<ICaptionService>(sp => new TelemetriaCaptionService(
                    sp.GetRequiredService<YouTubeCaptionService>(),
                    sp.GetRequiredService<ITelemetriaService>(),
                    sp.GetRequiredService<ILogger<TelemetriaCaptionService>>()));
                break;

            default: // YouTubeTranscript
                services.Configure<YouTubeTranscriptOptions>(config.GetSection("YouTubeTranscript"));
                services.AddSingleton<YouTubeTranscriptService>();
                services.AddSingleton<ICaptionService>(sp => new TelemetriaCaptionService(
                    sp.GetRequiredService<YouTubeTranscriptService>(),
                    sp.GetRequiredService<ITelemetriaService>(),
                    sp.GetRequiredService<ILogger<TelemetriaCaptionService>>()));
                break;
        }

        return services;
    }

    private static async Task<UserCredential> GetGoogleCredential(GoogleCredentialOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId))
            throw new InvalidOperationException("Google:ClientId não configurado");
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
            throw new InvalidOperationException("Google:ClientSecret não configurado");

        string[] scopes =
        [
            YouTubeService.Scope.Youtube,
            YouTubeService.Scope.YoutubeReadonly,
            YouTubeService.Scope.YoutubeForceSsl,
            "https://www.googleapis.com/auth/cloud-platform",
            "https://www.googleapis.com/auth/cloud-translation"
        ];

        var clientSecrets = new ClientSecrets { ClientId = options.ClientId, ClientSecret = options.ClientSecret };

        // Modo não-interativo (produção/cloud) — reusa um refresh token já emitido, sem abrir
        // navegador. Necessário porque os workers rodam headless (ex: ECS Fargate).
        if (!string.IsNullOrWhiteSpace(options.RefreshToken))
        {
            var flow = new GoogleAuthorizationCodeFlow(
                new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = clientSecrets, Scopes = scopes });

            var token = new TokenResponse { RefreshToken = options.RefreshToken };
            return new UserCredential(flow, options.User, token);
        }

        // Modo interativo (dev local) — abre navegador na primeira vez, cacheia o refresh token
        // num arquivo isolado por aplicação (Google:ApplicationName). Isolado porque Instituto e
        // Ministério usam Client Id/Secret diferentes — compartilhar o cache faz um sobrescrever
        // o token do outro.
        var dataStore = new FileDataStore($"Google.Apis.Auth.{options.ApplicationName}", true);

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            clientSecrets, scopes, options.User, CancellationToken.None, dataStore);
    }
}
