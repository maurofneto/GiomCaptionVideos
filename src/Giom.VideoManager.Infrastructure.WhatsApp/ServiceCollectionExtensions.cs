using Giom.VideoManager.Infrastructure.Resiliencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Giom.VideoManager.Infrastructure.WhatsApp;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra os TIPOS CONCRETOS, não IChatChannel — de propósito.
    ///
    /// O Telegram registra a si mesmo como IChatChannel (AddTelegramChatChannel), e o
    /// ChatBotProcessor hoje recebe UM único IChatChannel. Se este projeto também se registrasse
    /// na interface, os dois canais passariam a disputar a mesma resolução e o container
    /// entregaria em silêncio só o último registrado — o Telegram, que está em produção,
    /// simplesmente pararia de responder sem erro nenhum aparecer.
    ///
    /// Fazer os dois canais rodarem juntos é justamente o trabalho da task #141 (rodar N
    /// ChatBotProcessor, um por canal, via keyed services). Enquanto isso não existe, a ligação
    /// com a interface fica fora daqui.
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
        services.AddSingleton<WhatsAppWebhookProcessor>();

        return services;
    }
}
