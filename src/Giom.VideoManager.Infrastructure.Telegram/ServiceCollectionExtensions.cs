using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.Telegram;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramChatChannel(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<TelegramChatChannelOptions>(config.GetSection("Telegram"));
        services.AddSingleton<IChatChannel, TelegramChatChannel>();
        return services;
    }
}
