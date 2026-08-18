using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Observabilidade;
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
        // Uma instância só, exposta pela chave do canal: desde a US #24 o bot pode subir mais de
        // um canal ao mesmo tempo, e registrar direto em IChatChannel faria os canais disputarem
        // a mesma resolução — o container entregaria só o último registrado, em silêncio.
        services.AddSingleton<TelegramChatChannel>();
        services.AddKeyedSingleton<IChatChannel>(TelegramChatChannel.ChaveCanal, (sp, _) => sp.GetRequiredService<TelegramChatChannel>());

        // Bug #128: o long-polling (getUpdates) roda a cada ~30s pra sempre e sozinho já
        // respondia por 82% das transações do Sentry — só ruído, sem valor de observabilidade.
        // A URL da chamada também embute o token do bot em texto puro
        // (api.telegram.org/bot<TOKEN>/metodo) — mesmo com a exclusão acima, qualquer OUTRA
        // chamada real ao Telegram (SendMessage, SetMyCommands etc.) ainda vaza o token no nome
        // da transação, daí o scrubber além da regra de exclusão.
        services.AddSingleton<ISentryTransactionExclusionRule>(new SubstringSentryTransactionExclusionRule("/getUpdates"));
        services.AddSingleton<ISentryDataScrubber>(new RegexSentryDataScrubber(@"/bot\d+:[A-Za-z0-9_-]+", "/bot***"));

        return services;
    }
}
