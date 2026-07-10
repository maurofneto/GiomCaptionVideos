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
    /// Registra o YouTubeService autenticado com OAuth do usuário — usado tanto por quem
    /// descobre vídeos novos (AddYouTubeVideoSource) quanto por quem só busca legenda
    /// (YouTubeCaptionService, ver AddTranscriptionService na Composition). Separado de
    /// AddYouTubeVideoSource pra não obrigar um worker de etapa (ex: Transcricao.Worker) a
    /// registrar IVideoSourceService só pra ganhar a credencial. Idempotente — pode ser chamado
    /// por mais de um Add* na mesma composição.
    /// </summary>
    public static IServiceCollection AddYouTubeCredential(this IServiceCollection services, IConfiguration config)
    {
        if (services.Any(d => d.ServiceType == typeof(YouTubeService)))
            return services;

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

        return services;
    }

    /// <summary>Registra a credencial (ver AddYouTubeCredential) + o IVideoSourceService usado para descobrir vídeos novos nos canais monitorados.</summary>
    public static IServiceCollection AddYouTubeVideoSource(this IServiceCollection services, IConfiguration config)
    {
        services.AddYouTubeCredential(config);
        services.AddSingleton<IVideoSourceService, YouTubeVideoSourceService>();
        return services;
    }

    /// <summary>
    /// Variante de AddYouTubeVideoSource pra quando um único worker processa N canais numa run
    /// só (US #105, Ingestao.Worker) — não registra UMA credencial fixa (AddYouTubeCredential),
    /// porque cada canal pode ter Client Secret/Refresh Token diferentes. As credenciais keyed
    /// por canal são registradas à parte no bootstrap (ver AddIngestaoMultiCanal na
    /// Composition), usando BuildYouTubeService pra cada CanalIngestaoConfig lido do banco.
    /// YouTubeVideoSourceService resolve a keyed certa via IActiveChannelContext em runtime.
    /// </summary>
    public static IServiceCollection AddYouTubeVideoSourceMultiCanal(this IServiceCollection services)
    {
        services.AddSingleton<IVideoSourceService, YouTubeVideoSourceService>();
        return services;
    }

    /// <summary>
    /// Monta um YouTubeService a partir de uma credencial já resolvida (ClientId/ClientSecret/
    /// RefreshToken) — usado pelo bootstrap multi-canal (AddIngestaoMultiCanal) pra registrar um
    /// YouTubeService keyed por canal. Extraído de AddYouTubeCredential (que resolve só UMA
    /// credencial fixa por processo, do appsettings) pra reaproveitar a mesma lógica de auth
    /// com N credenciais diferentes, uma por canal, buscadas do SSM em runtime.
    /// </summary>
    public static YouTubeService BuildYouTubeService(GoogleCredentialOptions options)
    {
        var credential = GetGoogleCredential(options).GetAwaiter().GetResult();
        return new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = options.ApplicationName
        });
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
