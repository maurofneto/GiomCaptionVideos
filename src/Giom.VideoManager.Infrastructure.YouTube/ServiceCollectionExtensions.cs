using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
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
    /// Resolve a credencial Google e registra um YouTubeService keyed por canal (US #105) — cada
    /// CanalIngestaoConfig pode ter Client Secret/Refresh Token diferentes, buscados de
    /// "{SecretsKey}:Google:ClientSecret"/"RefreshToken" em IConfiguration (convenção do
    /// bootstrap multi-canal, ver Composition.AddIngestaoMultiCanal). Mantém todo detalhe de
    /// credencial Google (GoogleCredentialOptions, YouTubeService) dentro deste adapter —
    /// Composition só passa a lista de canais e recebe de volta só os que autenticaram OK.
    /// Falha ao resolver credencial de UM canal não derruba os demais — loga (via
    /// <paramref name="logger"/>, chega em Serilog/Sentry como qualquer outro log) e pula esse
    /// canal.
    /// </summary>
    public static IReadOnlyList<CanalIngestaoConfig> AddYouTubeCredenciaisPorCanal(
        this IServiceCollection services, IConfiguration config, IReadOnlyList<CanalIngestaoConfig> canais, ILogger logger)
    {
        var clientId = config["Google:ClientId"] ?? string.Empty;
        var user = config["Google:User"] ?? "user";
        var canaisValidos = new List<CanalIngestaoConfig>();

        foreach (var canal in canais)
        {
            try
            {
                var options = new GoogleCredentialOptions
                {
                    ClientId = clientId,
                    ClientSecret = config[$"{canal.SecretsKey}:Google:ClientSecret"] ?? string.Empty,
                    RefreshToken = config[$"{canal.SecretsKey}:Google:RefreshToken"],
                    // ApplicationName vira header HTTP (User-Agent) no cliente Google — precisa
                    // ser ASCII. canal.Nome é texto de exibição ("Ministério Giom", com acento) e
                    // quebra o cliente com FormatException; canal.SecretsKey é o slug ASCII
                    // ("instituto"/"ministerio"), igual ao ApplicationName fixo que os workers
                    // antigos usavam ("Giom Ministerio Processor").
                    ApplicationName = $"Giom {canal.SecretsKey} Processor",
                    User = user
                };

                var youtubeService = BuildYouTubeService(options);
                services.AddKeyedSingleton(canal.ChannelId, youtubeService);
                canaisValidos.Add(canal);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao resolver credencial do canal {CanalNome} ({ChannelId}) — pulando", canal.Nome, canal.ChannelId);
            }
        }

        return canaisValidos;
    }

    /// <summary>
    /// Monta um YouTubeService a partir de uma credencial já resolvida (ClientId/ClientSecret/
    /// RefreshToken) — extraído de AddYouTubeCredential (que resolve só UMA credencial fixa por
    /// processo, do appsettings) pra reaproveitar a mesma lógica de auth com N credenciais
    /// diferentes (ver AddYouTubeCredenciaisPorCanal).
    /// </summary>
    private static YouTubeService BuildYouTubeService(GoogleCredentialOptions options)
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
