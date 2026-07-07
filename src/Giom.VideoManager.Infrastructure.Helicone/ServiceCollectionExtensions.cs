using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.Helicone;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Liga o Helicone à sessão genérica (ISessionContext) — anexa headers de Session/User/
    /// Property/Request-Id no HttpClient "anthropic" (registrado por Infrastructure.Anthropic;
    /// chamar AddHttpClient de novo com o mesmo nome acrescenta à pipeline existente, não
    /// substitui) e registra IFeedbackExternoService. Nenhum tipo de IA (Claude*Service)
    /// referencia nada deste projeto — só ISessionContext/IFeedbackExternoService (Domain).
    /// </summary>
    public static IServiceCollection AddHelicone(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<HeliconeOptions>(config.GetSection("Helicone"));
        services.AddTransient<HeliconeHeadersHandler>();
        services.AddHttpClient("anthropic").AddHttpMessageHandler<HeliconeHeadersHandler>();

        services.AddHttpClient("helicone-api", client => client.BaseAddress = new Uri("https://api.helicone.ai/"));
        services.AddSingleton<IFeedbackExternoService, HeliconeFeedbackService>();

        return services;
    }
}
