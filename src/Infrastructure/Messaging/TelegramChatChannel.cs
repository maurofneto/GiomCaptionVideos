using Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Infrastructure.Messaging;

public class TelegramChatChannelOptions
{
    public string BotToken { get; init; } = string.Empty;
}

/// <summary>
/// Canal de chat via Telegram Bot API, usando long polling (getUpdates).
/// </summary>
public class TelegramChatChannel : IChatChannel
{
    private readonly ITelegramBotClient _bot;
    private readonly ILogger<TelegramChatChannel> _logger;

    public TelegramChatChannel(IOptions<TelegramChatChannelOptions> options, ILogger<TelegramChatChannel> logger)
    {
        _bot = new TelegramBotClient(options.Value.BotToken);
        _logger = logger;
    }

    public async IAsyncEnumerable<IncomingMessage> OuvirAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        int offset = 0;

        while (!ct.IsCancellationRequested)
        {
            Update[] updates;
            try
            {
                updates = await _bot.GetUpdates(offset, timeout: 30, cancellationToken: ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar updates do Telegram");
                await Task.Delay(5000, ct);
                continue;
            }

            foreach (var update in updates)
            {
                offset = update.Id + 1;

                var texto = update.Message?.Text;
                var chatId = update.Message?.Chat.Id;
                if (chatId is null || string.IsNullOrWhiteSpace(texto)) continue;

                yield return new IncomingMessage(chatId.Value.ToString(), texto, update.Message?.From?.Username);
            }
        }
    }

    public async Task ResponderAsync(string chatId, string texto, CancellationToken ct = default)
    {
        await _bot.SendMessage(long.Parse(chatId), texto, cancellationToken: ct);
    }
}
