using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Telemetria;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.YouTube;

/// <summary>
/// Credencial OAuth de UM canal, já resolvida da config. Sem fluxo interativo: os workers rodam
/// headless (ECS Fargate), então só existe o caminho "RefreshToken já emitido".
/// </summary>
public class GoogleCredentialOptions
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = "Giom Video Processor";
    public string User { get; init; } = "user";
    public string? RefreshToken { get; init; }
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o IVideoSourceService pra quando um único worker processa N canais numa run só
    /// (US #105, Ingestao.Worker) — não existe uma credencial fixa: cada canal tem a sua,
    /// registrada à parte no bootstrap (ver AddIngestaoMultiCanal na Composition, via
    /// AddYouTubeCredenciaisPorCanal). YouTubeVideoSourceService resolve a keyed certa via
    /// IActiveChannelContext em runtime.
    /// </summary>
    public static IServiceCollection AddYouTubeVideoSourceMultiCanal(this IServiceCollection services)
    {
        services.AddSingleton<IVideoSourceService, YouTubeVideoSourceService>();
        return services;
    }

    /// <summary>
    /// Legenda via YouTube Data API (Captions.list/download). Credencial OAuth: YouTubeService
    /// keyed por ChannelId (AddYouTubeCredenciaisPorCanal / US #146).
    /// </summary>
    public static IServiceCollection AddYouTubeCaption(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<YouTubeCaptionOptions>(config.GetSection("YouTubeCaption"));
        services.AddKeyedSingleton<ICaptionService, YouTubeCaptionService>("YouTubeCaption");
        return services;
    }

    /// <summary>Legenda via youtube-transcript (HTTP, sem OAuth / sem quota da Data API).</summary>
    public static IServiceCollection AddYouTubeTranscript(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<YouTubeTranscriptOptions>(config.GetSection("YouTubeTranscript"));
        services.AddKeyedSingleton<ICaptionService, YouTubeTranscriptService>("YouTubeTranscript");
        return services;
    }

    /// <summary>
    /// Legenda via yt-dlp. Registra <see cref="NullChannelYtDlpCookies"/> se ainda não houver
    /// <see cref="IChannelYtDlpCookies"/> — Composition/Smoke não precisam saber disso; o
    /// Transcricao.Worker substitui com <see cref="AddYtDlpCookiesPorCanal"/>.
    /// </summary>
    public static IServiceCollection AddYtDlpCaption(this IServiceCollection services, IConfiguration config)
    {
        if (!services.Any(d => d.ServiceType == typeof(IChannelYtDlpCookies)))
            services.AddSingleton<IChannelYtDlpCookies, NullChannelYtDlpCookies>();

        services.Configure<YtDlpCaptionOptions>(config.GetSection("YtDlpCaption"));
        services.AddKeyedSingleton<ICaptionService, YtDlpCaptionService>("YtDlpCaption");
        return services;
    }

    /// <summary>
    /// Escreve cookies Netscape por canal em arquivos temp e registra
    /// <see cref="IChannelYtDlpCookies"/> (US #146). Lê
    /// <c>{secrets_key}:YtDlp:CookiesContent</c>; fallback legado <c>YtDlp:CookiesContent</c>
    /// (pipeline, Bug #144). Substitui o Null de <see cref="AddYtDlpCaption"/>.
    /// </summary>
    public static IServiceCollection AddYtDlpCookiesPorCanal(
        this IServiceCollection services,
        IConfiguration configuration,
        IReadOnlyList<(string ChannelId, string SecretsKey)> canais,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(canais);
        ArgumentNullException.ThrowIfNull(logger);

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (channelId, secretsKey) in canais)
        {
            if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(secretsKey))
                continue;

            var content = configuration[$"{secretsKey}:YtDlp:CookiesContent"];
            var fromLegacy = false;
            if (string.IsNullOrWhiteSpace(content))
            {
                content = configuration["YtDlp:CookiesContent"];
                fromLegacy = !string.IsNullOrWhiteSpace(content);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogWarning(
                    "Sem cookies yt-dlp para ChannelId={ChannelId} (nem {{secrets_key}}:YtDlp:CookiesContent nem legado YtDlp:CookiesContent)",
                    channelId);
                continue;
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"yt-dlp-cookies-{SanitizeSecretsKey(secretsKey)}.txt");
            File.WriteAllText(filePath, content);
            paths[channelId] = filePath;

            logger.LogInformation(
                "Cookies yt-dlp carregados para ChannelId={ChannelId}, contentSha256Prefix={Hash}{Legacy}",
                channelId,
                ShortContentHash(content),
                fromLegacy ? " (legado pipeline)" : "");
        }

        if (paths.Count == 0)
        {
            logger.LogWarning(
                "Nenhum cookie yt-dlp por canal carregado. Configure {{secrets_key}}:YtDlp:CookiesContent no SSM canais/.");
        }

        services.AddSingleton<IChannelYtDlpCookies>(new ChannelYtDlpCookiesStore(paths));
        return services;
    }

    private static string SanitizeSecretsKey(string secretsKey) =>
        secretsKey.Replace(':', '-').Replace('/', '-');

    private static string ShortContentHash(string content)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    private sealed class ChannelYtDlpCookiesStore(IReadOnlyDictionary<string, string> paths) : IChannelYtDlpCookies
    {
        public string? GetCookiesFilePath(string channelId) =>
            string.IsNullOrWhiteSpace(channelId) ? null
            : paths.TryGetValue(channelId, out var path) ? path : null;
    }

    /// <summary>
    /// Resolve a credencial Google e registra um YouTubeService keyed por canal (US #105/#146).
    /// ÚNICA fonte de credencial: "{SecretsKey}:Google:ClientSecret"/"RefreshToken" (SSM
    /// /giom/{env}/canais/{secrets_key}/Google/*). ClientId: "{SecretsKey}:Google:ClientId" se
    /// existir, senão o compartilhado "Google:ClientId". As chaves sem prefixo
    /// "Google:ClientSecret"/"Google:RefreshToken" (legado SSM pipeline/Google) são ignoradas.
    /// Mantém todo detalhe de credencial Google dentro deste adapter — Composition só passa a
    /// lista de canais e recebe de volta só os que autenticaram OK.
    /// Falha de UM canal (ausente/vazia/invalid_grant/invalid_client) não derruba os demais: loga,
    /// guarda o motivo em <see cref="FalhasCredencialPorCanal"/> e pula o canal. Nunca abre fluxo
    /// interativo. <paramref name="aoFalhar"/> recebe cada canal descartado + a exceção: o log acima
    /// roda dentro de ConfigureServices, ANTES do SentrySdk.Init (SentryBootstrapHostedService),
    /// então não vira incidente no Sentry; quem chama usa o callback pra reportar depois (ver
    /// <see cref="AddReporteFalhasCredencialCanais"/>, Bug #147).
    /// </summary>
    public static IReadOnlyList<CanalIngestaoConfig> AddYouTubeCredenciaisPorCanal(
        this IServiceCollection services, IConfiguration config, IReadOnlyList<CanalIngestaoConfig> canais, ILogger logger,
        Action<CanalIngestaoConfig, Exception>? aoFalhar = null)
        => services.AddYouTubeCredenciaisPorCanal(config, canais, logger, RenovarCredencialAsync, aoFalhar);

    /// <summary>Costura de teste: <paramref name="fabricaCredencial"/> substitui o refresh de rede.</summary>
    internal static IReadOnlyList<CanalIngestaoConfig> AddYouTubeCredenciaisPorCanal(
        this IServiceCollection services,
        IConfiguration config,
        IReadOnlyList<CanalIngestaoConfig> canais,
        ILogger logger,
        Func<GoogleCredentialOptions, CancellationToken, Task<IConfigurableHttpClientInitializer>> fabricaCredencial,
        Action<CanalIngestaoConfig, Exception>? aoFalhar = null)
    {
        if (!string.IsNullOrWhiteSpace(config["Google:ClientSecret"]) || !string.IsNullOrWhiteSpace(config["Google:RefreshToken"]))
        {
            logger.LogWarning(
                "Chaves legadas Google:ClientSecret/Google:RefreshToken (SSM pipeline/Google) presentes na configuração e IGNORADAS — a credencial vem só de {{secrets_key}}:Google:* (SSM canais/)");
        }

        var clientIdCompartilhado = config["Google:ClientId"];
        var user = config["Google:User"] ?? "user";
        var canaisValidos = new List<CanalIngestaoConfig>();
        var falhas = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var canal in canais)
        {
            try
            {
                var clientIdPorCanal = config[$"{canal.SecretsKey}:Google:ClientId"];
                var porCanal = !string.IsNullOrWhiteSpace(clientIdPorCanal);

                var options = new GoogleCredentialOptions
                {
                    ClientId = (porCanal ? clientIdPorCanal : clientIdCompartilhado) ?? string.Empty,
                    ClientSecret = config[$"{canal.SecretsKey}:Google:ClientSecret"] ?? string.Empty,
                    RefreshToken = config[$"{canal.SecretsKey}:Google:RefreshToken"],
                    ApplicationName = $"{ApplicationNameFormatter.ParaAscii(canal.Nome)} Processor",
                    User = user
                };

                // clientId inteiro no log: o client Desktop (legado) e o Web (página de reconexão)
                // têm o MESMO número de projeto, então só "projeto=" não diferencia qual está em uso.
                logger.LogInformation(
                    "Credencial Google do canal {CanalNome} ({ChannelId}), secrets_key={SecretsKey}: ClientId origem={Origem}, projeto={Projeto}, clientId={ClientId}",
                    canal.Nome, canal.ChannelId, canal.SecretsKey, porCanal ? "por-canal" : "compartilhado", PrefixoProjeto(options.ClientId), options.ClientId);

                ExigirCampos(canal.ChannelId, canal.SecretsKey, options);

                var credencial = fabricaCredencial(options, CancellationToken.None).GetAwaiter().GetResult();

                // Hash curto do RefreshToken: permite provar nos logs que canais usam credenciais
                // distintas sem expor o valor.
                logger.LogInformation(
                    "Credencial Google OK para canal {CanalNome} ({ChannelId}), secrets_key={SecretsKey}, projeto={Projeto}, clientId={ClientId}, refreshTokenSha256Prefix={Hash}",
                    canal.Nome, canal.ChannelId, canal.SecretsKey, PrefixoProjeto(options.ClientId), options.ClientId, ShortContentHash(options.RefreshToken!));
                var youtubeService = new YouTubeService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credencial,
                    ApplicationName = options.ApplicationName
                });

                services.AddKeyedSingleton(canal.ChannelId, youtubeService);
                canaisValidos.Add(canal);
            }
            catch (Exception ex)
            {
                var motivo = ex is CredencialCanalIndisponivelException indisponivel ? indisponivel.Motivo : ResumirMotivo(ex);
                falhas[canal.ChannelId] = motivo;

                // Exceção crua só pro nosso tipo (texto próprio, sem segredo); qualquer outra —
                // inclusive TokenResponseException, cujo texto traz o error_description do Google —
                // pode carregar texto de terceiros: nesse caso só tipo + motivo.
                if (ex is CredencialCanalIndisponivelException)
                {
                    logger.LogError(ex, "Falha ao resolver credencial do canal {CanalNome} ({ChannelId}), secrets_key={SecretsKey} — pulando. Motivo: {Motivo}",
                        canal.Nome, canal.ChannelId, canal.SecretsKey, motivo);
                }
                else
                {
                    logger.LogError("Falha ao resolver credencial do canal {CanalNome} ({ChannelId}), secrets_key={SecretsKey} — pulando. Motivo: {Motivo}; Tipo: {Tipo}",
                        canal.Nome, canal.ChannelId, canal.SecretsKey, motivo, ex.GetType().Name);
                }

                aoFalhar?.Invoke(canal, ex);
            }
        }

        services.AddSingleton(new FalhasCredencialPorCanal(falhas));
        return canaisValidos;
    }

    /// <summary>
    /// Reporta (Error por canal com a exceção + Critical se NENHUM canal autenticou) só depois
    /// que todos os hosted services iniciaram — inclui o SentryBootstrapHostedService, então o
    /// sink do Serilog já encaminha pro Sentry. Não altera o exit code do processo (Bug #147).
    /// No-op se não houve falha.
    /// </summary>
    public static IServiceCollection AddReporteFalhasCredencialCanais(
        this IServiceCollection services, IReadOnlyList<FalhaCredencialCanal> falhas, int totalCanaisAtivos, int canaisValidos)
    {
        if (falhas.Count == 0) return services;

        services.AddSingleton<IHostedService>(sp => new ReporteFalhasCredencialCanais(
            falhas, totalCanaisAtivos, canaisValidos, sp.GetRequiredService<ILogger<ReporteFalhasCredencialCanais>>()));
        return services;
    }

    private static void ExigirCampos(string channelId, string secretsKey, GoogleCredentialOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId))
            throw new CredencialCanalIndisponivelException(channelId, $"ClientId ausente (nem {secretsKey}:Google:ClientId nem Google:ClientId)");
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
            throw new CredencialCanalIndisponivelException(channelId, $"ClientSecret ausente ({secretsKey}:Google:ClientSecret)");
        if (string.IsNullOrWhiteSpace(options.RefreshToken))
            throw new CredencialCanalIndisponivelException(channelId, $"RefreshToken ausente ({secretsKey}:Google:RefreshToken)");
    }

    /// <summary>
    /// Só a parte numérica antes do primeiro '-' (número do projeto Google). NÃO identifica o client:
    /// Desktop e Web do mesmo projeto compartilham o número — por isso o log também traz o
    /// clientId inteiro (identificador público, aparece na URL de autorização). ClientSecret e
    /// RefreshToken nunca vão pro log.
    /// </summary>
    private static string PrefixoProjeto(string clientId)
    {
        var idx = clientId.IndexOf('-');
        var prefixo = idx > 0 ? clientId[..idx] : string.Empty;
        return prefixo.Length > 0 && prefixo.All(char.IsDigit) ? prefixo : "indisponivel";
    }

    /// <summary>
    /// Resumo sem segredo: código OAuth (invalid_grant/unauthorized_client/invalid_client) ou só o
    /// tipo da exceção. O error_description cru do Google NÃO entra (texto de terceiros).
    /// </summary>
    private static string ResumirMotivo(Exception ex) => ex is TokenResponseException token
        ? $"refresh recusado pelo Google ({token.Error?.Error ?? "erro desconhecido"}); causa provável: token revogado/expirado "
          + "ou token emitido por outro client OAuth (ClientId diferente do usado na reconexão) — "
          + "confira clientId= no log de boot e reconecte o canal pela página de reconexão"
        : $"falha ao renovar token ({ex.GetType().Name})";

    private static async Task<IConfigurableHttpClientInitializer> RenovarCredencialAsync(GoogleCredentialOptions options, CancellationToken ct)
    {
        string[] scopes =
        [
            YouTubeService.Scope.Youtube,
            YouTubeService.Scope.YoutubeReadonly,
            YouTubeService.Scope.YoutubeForceSsl,
            "https://www.googleapis.com/auth/cloud-platform",
            "https://www.googleapis.com/auth/cloud-translation"
        ];

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = options.ClientId, ClientSecret = options.ClientSecret },
            Scopes = scopes
        });

        var credential = new UserCredential(flow, options.User, new TokenResponse { RefreshToken = options.RefreshToken });

        // O construtor não valida nada — o token morto só apareceria na primeira chamada de API,
        // no meio do processamento (visto em produção: invalid_grant). Testa aqui, no boot, pra
        // pular só este canal. Nunca cai em GoogleWebAuthorizationBroker (sem navegador no ECS).
        await credential.RefreshTokenAsync(ct);
        return credential;
    }
}
