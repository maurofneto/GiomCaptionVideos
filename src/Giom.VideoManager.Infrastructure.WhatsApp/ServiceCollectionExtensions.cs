using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.WhatsApp;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o canal sob a SUA chave (keyed service), nunca direto em IChatChannel: desde a
    /// US #24 o bot sobe Telegram e WhatsApp no mesmo processo, e uma ligação direta na interface
    /// faria os dois disputarem a mesma resolução — o container entregaria só o último
    /// registrado, sem erro nenhum aparecer, e um dos canais ficaria mudo.
    /// </summary>
    public static IServiceCollection AddWhatsAppChatChannel(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<WhatsAppOptions>(config.GetSection("WhatsApp"));

        services.AddHttpClient("whatsapp", client =>
        {
            client.BaseAddress = new Uri("https://graph.facebook.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).AddStandardResilienceHandler(options => options.RegistrarEventoDeRetryNoSpan("WhatsApp"));

        // Singleton: a fila em memória (WhatsAppInbox) SÓ funciona como ponte se o endpoint do
        // webhook e o canal enxergarem a mesma instância.
        services.AddSingleton<IWhatsAppInbox, WhatsAppInbox>();
        services.AddSingleton<WhatsAppChatChannel>();
        services.AddKeyedSingleton<IChatChannel>(WhatsAppChatChannel.ChaveCanal, (sp, _) => sp.GetRequiredService<WhatsAppChatChannel>());
        services.AddSingleton<WhatsAppWebhookProcessor>();

        return services;
    }
}
