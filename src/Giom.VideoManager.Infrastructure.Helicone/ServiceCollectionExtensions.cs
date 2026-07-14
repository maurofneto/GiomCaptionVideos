using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Helicone;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Liga o Helicone à sessão genérica (ISessionContext) nos HttpClient "anthropic"/"openai"/
    /// "grok" (registrados por Infrastructure.Anthropic/OpenAI/Grok; chamar AddHttpClient de
    /// novo com o mesmo nome acrescenta à pipeline existente, não substitui): anexa headers de
    /// Session/User/Property/Request-Id (HeliconeHeadersHandler) e o roteamento pro gateway
    /// (HeliconeRoteamentoHandler, US #106 — decide por chamada via
    /// ISessionContext.Atual.UsarObservabilidade, não mais fixado na construção do client).
    /// Anthropic e OpenAI têm domínio dedicado no Helicone; xAI/Grok não, então passa pelo
    /// Gateway genérico (Helicone-Target-Url/-Provider) — ver HeliconeRota. Também registra
    /// IFeedbackExternoService. Nenhum tipo de IA (Claude/Grok/OpenAi*Service) referencia nada
    /// deste projeto — só ISessionContext/IFeedbackExternoService (Domain).
    /// </summary>
    public static IServiceCollection AddHelicone(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<HeliconeOptions>(config.GetSection("Helicone"));
        services.AddTransient<HeliconeHeadersHandler>();

        AdicionarRoteamento(services, "anthropic", HeliconeRota.DominioDedicado("anthropic.helicone.ai"));
        AdicionarRoteamento(services, "openai", HeliconeRota.DominioDedicado("oai.helicone.ai"));
        // xAI/Grok não tem domínio dedicado confirmado no Helicone — Gateway genérico é o
        // caminho garantido (funciona pra qualquer provedor). "X-AI" é a melhor tentativa pro
        // valor de Helicone-Target-Provider; não confirmado contra o dashboard real — se o
        // Helicone rejeitar/não reconhecer, é só ajustar essa string, nada de código muda.
        AdicionarRoteamento(services, "grok", HeliconeRota.GatewayGenerico("X-AI"));

        services.AddHttpClient("helicone-api", client => client.BaseAddress = new Uri("https://api.helicone.ai/"));
        services.AddSingleton<IFeedbackExternoService, HeliconeFeedbackService>();

        return services;
    }

    private static void AdicionarRoteamento(IServiceCollection services, string nomeHttpClient, HeliconeRota rota)
    {
        services.AddHttpClient(nomeHttpClient)
            .AddHttpMessageHandler<HeliconeHeadersHandler>()
            .AddHttpMessageHandler(sp => new HeliconeRoteamentoHandler(
                sp.GetRequiredService<ISessionContext>(),
                sp.GetRequiredService<IOptions<HeliconeOptions>>(),
                rota));
    }
}
