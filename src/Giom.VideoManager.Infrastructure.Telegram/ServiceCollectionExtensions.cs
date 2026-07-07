using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace Giom.VideoManager.Infrastructure.Telegram;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramChatChannel(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<TelegramChatChannelOptions>(config.GetSection("Telegram"));
        // Construído aqui (não dentro de TelegramChatChannel) — deixa a classe testável com um
        // ITelegramBotClient fake, sem token nem rede de verdade.
        services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(sp.GetRequiredService<IOptions<TelegramChatChannelOptions>>().Value.BotToken));
        services.AddSingleton<IChatChannel, TelegramChatChannel>();
        return services;
    }
}
