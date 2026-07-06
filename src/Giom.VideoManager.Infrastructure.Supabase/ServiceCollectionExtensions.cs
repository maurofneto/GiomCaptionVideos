using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.Supabase;

public static class ServiceCollectionExtensions
{
    private sealed class SupabaseHttpClientMarker;

    private static IServiceCollection AddSupabaseHttpClient(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(SupabaseHttpClientMarker)))
            return services;

        services.AddSingleton<SupabaseHttpClientMarker>();
        services.AddHttpClient("supabase").AddStandardResilienceHandler();
        return services;
    }

    public static IServiceCollection AddSupabasePersistence(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseOptions>(config.GetSection("Supabase"));
        services.AddSupabaseHttpClient();
        services.AddSingleton<IVideoRepository, SupabaseVideoRepository>();
        services.AddSingleton<IProgressoRepository, SupabaseProgressoRepository>();
        services.AddSingleton<IVideoChunkRepository, SupabaseVideoChunkRepository>();
        return services;
    }

    public static IServiceCollection AddSupabaseEmbedding(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseEmbeddingOptions>(config.GetSection("Supabase"));
        services.AddSingleton<SupabaseEmbeddingService>();
        return services;
    }

    /// <summary>Histórico de conversa persistido — sobrevive a reinícios do worker.</summary>
    public static IServiceCollection AddSupabaseConversaHistorico(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseOptions>(config.GetSection("Supabase"));
        services.AddSupabaseHttpClient();
        services.AddSingleton<IConversaHistoricoService, SupabaseConversaHistoricoService>();
        return services;
    }

    /// <summary>
    /// Auditoria genérica de operações de IA (resumo, categorização, bot) — tabela interacao_log.
    /// Registra só o tipo concreto (não o ITelemetriaService diretamente) — quem decide como
    /// isso vira o ITelemetriaService de fato injetado no app é a composição raiz (ver
    /// Composition.AddTelemetriaService), que combina esse sink com outros (ex: Sentry) via
    /// CompositeTelemetriaService.
    /// </summary>
    public static IServiceCollection AddSupabaseTelemetria(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseOptions>(config.GetSection("Supabase"));
        services.AddSupabaseHttpClient();
        services.AddSingleton<SupabaseTelemetriaService>();
        return services;
    }
}
