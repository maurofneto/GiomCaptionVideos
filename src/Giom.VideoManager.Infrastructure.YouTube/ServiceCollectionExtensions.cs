using Giom.VideoManager.Domain.Interfaces;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.YouTube;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o YouTubeService autenticado com OAuth do usuário e o IVideoSourceService
    /// usado para descobrir vídeos novos nos canais monitorados.
    /// </summary>
    public static IServiceCollection AddYouTubeVideoSource(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(_ => GetGoogleCredential(config).GetAwaiter().GetResult());

        services.AddSingleton(provider =>
        {
            var cred = provider.GetRequiredService<UserCredential>();
            return new YouTubeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = cred,
                ApplicationName = config["Google:ApplicationName"] ?? "Giom Video Processor"
            });
        });

        services.AddSingleton<IVideoSourceService, YouTubeVideoSourceService>();
        return services;
    }

    /// <summary>
    /// Registra ICaptionService conforme Transcription:Provider.
    /// Valores aceitos: YouTubeCaption, YtDlpCaption, YouTubeTranscript (padrão — sem quota, sem processo externo).
    /// </summary>
    public static IServiceCollection AddYouTubeCaptionService(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Transcription:Provider"] ?? "YouTubeTranscript";

        switch (provider)
        {
            case "YtDlpCaption":
                services.Configure<YtDlpCaptionOptions>(config.GetSection("YtDlpCaption"));
                services.AddSingleton<ICaptionService, YtDlpCaptionService>();
                break;

            case "YouTubeCaption":
                services.Configure<YouTubeCaptionOptions>(config.GetSection("YouTubeCaption"));
                services.AddSingleton<ICaptionService, YouTubeCaptionService>();
                break;

            default: // YouTubeTranscript
                services.Configure<YouTubeTranscriptOptions>(config.GetSection("YouTubeTranscript"));
                services.AddSingleton<ICaptionService, YouTubeTranscriptService>();
                break;
        }

        return services;
    }

    private static async Task<UserCredential> GetGoogleCredential(IConfiguration config)
    {
        var clientId = config["Google:ClientId"]
            ?? throw new InvalidOperationException("Google:ClientId não configurado");
        var clientSecret = config["Google:ClientSecret"]
            ?? throw new InvalidOperationException("Google:ClientSecret não configurado");

        string[] scopes =
        [
            YouTubeService.Scope.Youtube,
            YouTubeService.Scope.YoutubeReadonly,
            YouTubeService.Scope.YoutubeForceSsl,
            "https://www.googleapis.com/auth/cloud-platform",
            "https://www.googleapis.com/auth/cloud-translation"
        ];

        var clientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
        var user = config["Google:User"] ?? "user";

        // Modo não-interativo (produção/cloud) — reusa um refresh token já emitido, sem abrir
        // navegador. Necessário porque os workers rodam headless (ex: ECS Fargate).
        var refreshToken = config["Google:RefreshToken"];
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var flow = new GoogleAuthorizationCodeFlow(
                new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = clientSecrets, Scopes = scopes });

            var token = new TokenResponse { RefreshToken = refreshToken };
            return new UserCredential(flow, user, token);
        }

        // Modo interativo (dev local) — abre navegador na primeira vez, cacheia o refresh token
        // num arquivo isolado por aplicação (Google:ApplicationName). Isolado porque Instituto e
        // Ministério usam Client Id/Secret diferentes — compartilhar o cache faz um sobrescrever
        // o token do outro.
        var appName = config["Google:ApplicationName"] ?? "GiomWorker";
        var dataStore = new FileDataStore($"Google.Apis.Auth.{appName}", true);

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            clientSecrets, scopes, user, CancellationToken.None, dataStore);
    }
}
