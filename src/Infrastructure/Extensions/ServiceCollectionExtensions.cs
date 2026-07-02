using Application.Options;
using Domain.Interfaces;
using Google.Apis.Auth.OAuth2;
using Infrastructure.Categorization;
using Infrastructure.Embedding;
using Infrastructure.Helpers;
using Infrastructure.Persistence.MongoDB;
using Infrastructure.Persistence.SqlServer;
using Infrastructure.Persistence.Supabase;
using Infrastructure.Summary;
using Infrastructure.Transcription;
using Infrastructure.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPromptLoader(this IServiceCollection services)
    {
        services.AddSingleton<PromptLoader>();
        return services;
    }

    /// <summary>
    /// Registra named HttpClients com Polly resilience para todos os serviços de infraestrutura.
    /// Deve ser chamado antes de AddPersistence, AddSummaryService, AddEmbeddingService.
    /// </summary>
    public static IServiceCollection AddInfrastructureHttpClients(this IServiceCollection services)
    {
        // Supabase PostgREST — base URL e auth configurados per-service (variam por ambiente)
        services.AddHttpClient("supabase").AddStandardResilienceHandler();

        // Supabase Edge Functions
        services.AddHttpClient("supabase-functions").AddStandardResilienceHandler();

        // OpenAI Embeddings API
        services.AddHttpClient("openai", client =>
        {
            client.BaseAddress = new Uri("https://api.openai.com/");
            client.Timeout = TimeSpan.FromMinutes(5);
        }).AddStandardResilienceHandler();

        // Anthropic (SDK gerencia BaseAddress e auth; factory provê pooling de sockets)
        // Timeouts maiores que o padrão (10s/tentativa, 30s total) — transcrições longas
        // fazem o Claude demorar mais para gerar o resumo completo.
        services.AddHttpClient("anthropic").AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(200);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        });

        return services;
    }

    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Persistence:Provider"] ?? "SqlServer";

        switch (provider)
        {
            case "Supabase":
                services.Configure<SupabaseOptions>(config.GetSection("Supabase"));
                services.AddSingleton<IVideoRepository, SupabaseVideoRepository>();
                services.AddSingleton<IProgressoRepository, SupabaseProgressoRepository>();
                services.AddSingleton<IVideoChunkRepository, SupabaseVideoChunkRepository>();
                break;

            case "MongoDB":
                services.Configure<MongoOptions>(config.GetSection("MongoDB"));
                services.AddSingleton<IVideoRepository, MongoVideoRepository>();
                services.AddSingleton<IProgressoRepository, MongoProgressoRepository>();
                break;

            default: // SqlServer
                services.AddSingleton<IVideoRepository, SqlVideoRepository>();
                services.AddSingleton<IProgressoRepository, SqlProgressoRepository>();
                break;
        }

        return services;
    }

    /// <summary>
    /// Registra ICaptionService ou ITranscricaoService conforme Transcription:Provider.
    /// Valores aceitos: YouTubeCaption, YtDlpCaption, YouTubeTranscript (padrão), Whisper
    /// </summary>
    public static IServiceCollection AddTranscriptionService(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Transcription:Provider"] ?? "YouTubeTranscript";

        switch (provider)
        {
            case "Whisper":
                services.Configure<WhisperOptions>(config.GetSection("Whisper"));
                services.AddSingleton<ITranscricaoService, WhisperTranscricaoService>();
                break;

            case "YtDlpCaption":
                services.Configure<YtDlpCaptionOptions>(config.GetSection("YtDlpCaption"));
                services.AddSingleton<ICaptionService, YtDlpCaptionService>();
                break;

            case "YouTubeCaption":
                services.Configure<YouTubeCaptionOptions>(config.GetSection("YouTubeCaption"));
                services.AddSingleton<ICaptionService, YouTubeCaptionService>();
                break;

            default: // YouTubeTranscript — C# puro, sem quota, sem processo externo
                services.Configure<YouTubeTranscriptOptions>(config.GetSection("YouTubeTranscript"));
                services.AddSingleton<ICaptionService, YouTubeTranscriptService>();
                break;
        }

        return services;
    }

    /// <summary>
    /// Registra IResumoService conforme Summary:Provider.
    /// Valores aceitos: Claude (padrão), SummarizeTech
    /// </summary>
    public static IServiceCollection AddSummaryService(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Summary:Provider"] ?? "Claude";

        if (provider == "SummarizeTech")
        {
            services.AddSingleton<IResumoService, SummarizeTechResumoService>();
        }
        else
        {
            services.Configure<ClaudeResumoOptions>(config.GetSection("Claude"));
            services.AddSingleton<IResumoService, ClaudeResumoService>();
        }

        return services;
    }

    /// <summary>
    /// Registra ICategorizacaoService (sempre Claude por enquanto).
    /// </summary>
    public static IServiceCollection AddCategorizacaoService(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ClaudeCategorizacaoOptions>(config.GetSection("Claude"));
        services.AddSingleton<ICategorizacaoService, ClaudeCategorizacaoService>();
        return services;
    }

    /// <summary>
    /// Registra IEmbeddingService conforme Embedding:Provider.
    /// Valores aceitos: Direct (padrão), SupabaseEdgeFunction
    /// </summary>
    public static IServiceCollection AddEmbeddingService(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Embedding:Provider"] ?? "Direct";

        if (provider == "SupabaseEdgeFunction")
        {
            services.Configure<SupabaseEmbeddingOptions>(config.GetSection("Supabase"));
            services.AddSingleton<IEmbeddingService, SupabaseEmbeddingService>();
        }
        else // Direct — C# chama OpenAI e insere no Supabase via IVideoChunkRepository
        {
            services.Configure<DirectEmbeddingOptions>(config.GetSection("DirectEmbedding"));
            services.AddSingleton<IEmbeddingService, DirectEmbeddingService>();
        }

        return services;
    }

    /// <summary>
    /// Registra o YouTubeService autenticado com OAuth do usuário.
    /// </summary>
    public static IServiceCollection AddYouTubeServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(provider =>
        {
            var cred = GetGoogleCredential(config).GetAwaiter().GetResult();
            return cred;
        });

        services.AddSingleton(provider =>
        {
            var cred = provider.GetRequiredService<Google.Apis.Auth.OAuth2.UserCredential>();
            return new Google.Apis.YouTube.v3.YouTubeService(new Google.Apis.Services.BaseClientService.Initializer
            {
                HttpClientInitializer = cred,
                ApplicationName = config["Google:ApplicationName"] ?? "Giom Video Processor"
            });
        });

        services.AddSingleton<IVideoSourceService, YouTubeVideoSourceService>();
        return services;
    }

    /// <summary>
    /// Registra as opções de processamento e o ChannelProcessor.
    /// </summary>
    public static IServiceCollection AddChannelProcessor(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ProcessingOptions>(config.GetSection("ProcessingOptions"));
        services.AddSingleton<Application.Pipeline.ChannelProcessor>();
        return services;
    }

    private static async Task<Google.Apis.Auth.OAuth2.UserCredential> GetGoogleCredential(IConfiguration config)
    {
        var clientId = config["Google:ClientId"]
            ?? throw new InvalidOperationException("Google:ClientId não configurado");
        var clientSecret = config["Google:ClientSecret"]
            ?? throw new InvalidOperationException("Google:ClientSecret não configurado");

        string[] scopes =
        [
            Google.Apis.YouTube.v3.YouTubeService.Scope.Youtube,
            Google.Apis.YouTube.v3.YouTubeService.Scope.YoutubeReadonly,
            Google.Apis.YouTube.v3.YouTubeService.Scope.YoutubeForceSsl,
            "https://www.googleapis.com/auth/cloud-platform",
            "https://www.googleapis.com/auth/cloud-translation"
        ];

        var clientSecrets = new Google.Apis.Auth.OAuth2.ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
        var user = config["Google:User"] ?? "user";

        // Modo não-interativo (produção/cloud) — reusa um refresh token já emitido, sem abrir
        // navegador. Necessário porque os workers rodam headless (ex: ECS Fargate).
        var refreshToken = config["Google:RefreshToken"];
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var flow = new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow(
                new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = clientSecrets,
                    Scopes = scopes
                });

            var token = new Google.Apis.Auth.OAuth2.Responses.TokenResponse { RefreshToken = refreshToken };
            return new Google.Apis.Auth.OAuth2.UserCredential(flow, user, token);
        }

        // Modo interativo (dev local) — abre navegador na primeira vez, cacheia o refresh token
        // num arquivo isolado por aplicação (Google:ApplicationName). Isolado porque Instituto e
        // Ministério usam Client Id/Secret diferentes — compartilhar o cache faz um sobrescrever
        // o token do outro.
        var appName   = config["Google:ApplicationName"] ?? "GiomWorker";
        var dataStore = new Google.Apis.Util.Store.FileDataStore($"Google.Apis.Auth.{appName}", true);

        var credential = await Google.Apis.Auth.OAuth2.GoogleWebAuthorizationBroker.AuthorizeAsync(
            clientSecrets,
            scopes,
            user,
            CancellationToken.None,
            dataStore);

        return credential;
    }
}
