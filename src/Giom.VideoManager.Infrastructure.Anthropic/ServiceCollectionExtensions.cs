using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;

namespace Giom.VideoManager.Infrastructure.Anthropic;

public static class ServiceCollectionExtensions
{
    private sealed class AnthropicHttpClientMarker;

    private static IServiceCollection AddAnthropicHttpClient(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(AnthropicHttpClientMarker)))
            return services;

        services.AddSingleton<AnthropicHttpClientMarker>();
        services.AddHttpClient("anthropic").AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(200);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        });

        return services;
    }

    public static IServiceCollection AddClaudeAgent(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddAnthropicHttpClient();
        services.Configure<ClaudeAgentOptions>(config.GetSection("ClaudeAgent"));
        services.AddSingleton<IAgentService, ClaudeAgentService>();
        return services;
    }

    public static IServiceCollection AddClaudeSummary(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddAnthropicHttpClient();
        services.Configure<ClaudeResumoOptions>(config.GetSection("ClaudeResumo"));
        services.AddSingleton<IResumoService, ClaudeResumoService>();
        return services;
    }

    public static IServiceCollection AddClaudeCategorizacao(this IServiceCollection services, IConfiguration config)
    {
        services.AddPromptLoader();
        services.AddAnthropicHttpClient();
        services.Configure<ClaudeCategorizacaoOptions>(config.GetSection("ClaudeCategorizacao"));
        services.AddSingleton<ICategorizacaoService, ClaudeCategorizacaoService>();
        return services;
    }
}
