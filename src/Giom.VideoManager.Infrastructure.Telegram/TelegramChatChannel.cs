using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Giom.VideoManager.Infrastructure.Telegram;

public class TelegramChatChannelOptions
{
    public string BotToken { get; init; } = string.Empty;
}

/// <summary>
/// Canal de chat via Telegram Bot API, usando long polling (getUpdates).
/// </summary>
public class TelegramChatChannel : IChatChannel
{
    private static readonly Regex TagHtml = new(@"<[^>]+>", RegexOptions.Compiled);

    // Só o menu de autocomplete do "/" no cliente Telegram — os comandos em si são
    // tratados pelo ChatBotProcessor (Application), que funciona em qualquer canal.
    private static readonly BotCommand[] ComandosPadrao =
    [
        new() { Command = "ajuda", Description = "Mostra os comandos disponíveis" },
        new() { Command = "sobre", Description = "Sobre este bot" },
        new() { Command = "limpar", Description = "Apaga o histórico da conversa e recomeça" },
    ];

    private readonly ITelegramBotClient _bot;
    private readonly IBotComandosService _comandos;
    private readonly ILogger<TelegramChatChannel> _logger;

    public TelegramChatChannel(IOptions<TelegramChatChannelOptions> options, IBotComandosService comandos, ILogger<TelegramChatChannel> logger)
    {
        _bot = new TelegramBotClient(options.Value.BotToken);
        _comandos = comandos;
        _logger = logger;
    }

    public async IAsyncEnumerable<IncomingMessage> OuvirAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            await _bot.SetMyCommands(ComandosPadrao, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível registrar a lista de comandos no Telegram");
        }

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

                var mensagem = update.Message;
                var texto = mensagem?.Text;
                var chatId = mensagem?.Chat.Id;
                if (chatId is null || string.IsNullOrWhiteSpace(texto)) continue;

                var chatIdStr = chatId.Value.ToString();

                if (await TratarComandoAsync(mensagem!, chatIdStr, texto, ct))
                    continue;

                yield return new IncomingMessage(chatIdStr, texto, mensagem?.From?.Username);
            }
        }
    }

    /// <summary>
    /// O Telegram marca comandos com uma entidade BotCommand própria (não é um simples
    /// "texto começa com /") — usamos essa marcação nativa do canal pra decidir o que é
    /// comando, e delegamos a ação ao IBotComandosService (Domain), que não sabe nada de Telegram.
    /// </summary>
    private async Task<bool> TratarComandoAsync(Message mensagem, string chatId, string texto, CancellationToken ct)
    {
        var entidade = mensagem.Entities?.FirstOrDefault(e => e.Type == MessageEntityType.BotCommand && e.Offset == 0);
        if (entidade is null) return false;

        var comando = texto.Substring(entidade.Offset, entidade.Length);
        var arroba = comando.IndexOf('@'); // em grupos o Telegram sufixa "@nomedobot" ao comando
        if (arroba >= 0) comando = comando[..arroba];

        var resposta = comando.ToLowerInvariant() switch
        {
            "/start" => _comandos.ObterBoasVindas(),
            "/ajuda" or "/help" => _comandos.ObterAjuda(),
            "/sobre" => _comandos.ObterSobre(),
            "/limpar" or "/reset" => await _comandos.LimparAsync(chatId, ct),
            _ => $"Não conheço o comando {comando}. Manda /ajuda pra ver o que eu sei fazer."
        };

        await ResponderAsync(chatId, resposta, ct);
        return true;
    }

    public async Task ResponderAsync(string chatId, string texto, CancellationToken ct = default)
    {
        var id = long.Parse(chatId);

        // O agente escreve em markdown simples, sem saber que o canal é o Telegram —
        // a conversão para o formato de exibição é responsabilidade deste canal.
        var html = TelegramFormatador.ParaHtml(texto);

        try
        {
            await _bot.SendMessage(id, html, parseMode: ParseMode.Html, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // HTML do Telegram só aceita um subconjunto de tags e não perdoa marcação malformada —
            // se o texto gerado quebrar o parser, reenvia sem formatação a perder a resposta.
            _logger.LogWarning(ex, "Falha ao enviar mensagem formatada (HTML) para {ChatId} — reenviando como texto simples", chatId);
            await _bot.SendMessage(id, TagHtml.Replace(texto, string.Empty), cancellationToken: ct);
        }
    }
}
