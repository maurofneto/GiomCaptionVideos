using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Giom.VideoManager.Infrastructure.Supabase;

public static class ServiceCollectionExtensions
{
    private sealed class SupabaseHttpClientMarker;

    private static IServiceCollection AddSupabaseHttpClient(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(SupabaseHttpClientMarker)))
            return services;

        services.AddSingleton<SupabaseHttpClientMarker>();
        services.AddHttpClient("supabase").AddStandardResilienceHandler(options => options.RegistrarEventoDeRetryNoSpan("Supabase"));
        return services;
    }

    public static IServiceCollection AddSupabasePersistence(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseOptions>(config.GetSection("Supabase"));
        services.AddSupabaseHttpClient();
        services.AddSingleton<IVideoRepository, SupabaseVideoRepository>();
        services.AddSingleton<IProgressoRepository, SupabaseProgressoRepository>();
        services.AddSingleton<IVideoChunkRepository, SupabaseVideoChunkRepository>();
        services.AddSingleton<IVideoEtapaTerminalRepository, SupabaseVideoEtapaTerminalRepository>();
        return services;
    }

    public static IServiceCollection AddSupabaseEmbedding(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseEmbeddingOptions>(config.GetSection("Supabase"));
        services.AddSingleton<IEmbeddingService, SupabaseEmbeddingService>();
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
    /// Registrado como ITelemetriaSink (não ITelemetriaService) — CompositeTelemetriaService
    /// agrega todo ITelemetriaSink automaticamente via DI (ver Composition.AddTelemetriaService).
    /// </summary>
    public static IServiceCollection AddSupabaseTelemetria(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseOptions>(config.GetSection("Supabase"));
        services.AddSupabaseHttpClient();
        services.AddSingleton<ITelemetriaSink, SupabaseTelemetriaService>();
        return services;
    }

    /// <summary>
    /// Registro normal de ICanalIngestaoRepository (US #105) pra quem quiser injetar via DI. O
    /// bootstrap do Ingestao.Worker (AddIngestaoMultiCanal, Composition) constrói uma instância
    /// direto — precisa da lista de canais ANTES do container terminar de montar, pra registrar
    /// as credenciais Google keyed por canal.
    /// </summary>
    public static IServiceCollection AddSupabaseCanalIngestao(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseOptions>(config.GetSection("Supabase"));
        services.AddSingleton<ICanalIngestaoRepository, SupabaseCanalIngestaoRepository>();
        return services;
    }

    /// <summary>Registro de IEtapaProvedorConfigRepository (US #106) — leitura da tabela etapa_provedor_config.</summary>
    public static IServiceCollection AddSupabaseEtapaProvedorConfig(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseOptions>(config.GetSection("Supabase"));
        services.AddSingleton<IEtapaProvedorConfigRepository, SupabaseEtapaProvedorConfigRepository>();
        return services;
    }

    /// <summary>Registro de IPricingRepository (Task #130) — leitura da tabela provedor_preco.</summary>
    public static IServiceCollection AddSupabasePricing(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseOptions>(config.GetSection("Supabase"));
        services.AddSingleton<IPricingRepository, SupabasePricingRepository>();
        return services;
    }

    /// <summary>
    /// Farol de rate limit por provedor (Bug #122) — token bucket compartilhado via Postgres
    /// (tabela provedor_rate_limit). TryAddSingleton: chamado uma vez por provedor de embedding
    /// que precisa dele (Voyage, Cohere) — idempotente, seguro chamar mais de uma vez na mesma
    /// composição sem duplicar a instância.
    /// </summary>
    public static IServiceCollection AddSupabaseFarolRateLimiter(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SupabaseOptions>(config.GetSection("Supabase"));
        services.AddSupabaseHttpClient();
        services.TryAddSingleton<IFarolRateLimiter, SupabaseFarolRateLimiter>();
        return services;
    }
}
