using Application.Options;
using Domain.Interfaces;
using Google.Apis.Auth.OAuth2;
using Infrastructure.Categorization;
using Infrastructure.Embedding;
using Infrastructure.Persistence.MongoDB;
using Infrastructure.Persistence.SqlServer;
using Infrastructure.Persistence.Supabase;
using Infrastructure.Summary;
using Infrastructure.Transcription;
using Infrastructure.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra IVideoRepository e IProgressoRepository conforme Persistence:Provider no appsettings.
    /// Valores aceitos: SqlServer (padrão), Supabase, MongoDB
    /// </summary>
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

        var credential = await Google.Apis.Auth.OAuth2.GoogleWebAuthorizationBroker.AuthorizeAsync(
            new Google.Apis.Auth.OAuth2.ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            scopes,
            config["Google:User"] ?? "user",
            CancellationToken.None);

        //await GoogleWebAuthorizationBroker.ReauthorizeAsync(credential, CancellationToken.None);

        return credential;
    }
}
