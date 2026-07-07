using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

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

    // Telegram rejeita mensagens de texto acima de 4096 caracteres — margem de segurança
    // porque a conversão pra HTML (negrito/itálico) ainda vai acrescentar alguns caracteres.
    private const int LimiteSeguroPorMensagem = 3800;

    // Só o menu de autocomplete do "/" no cliente Telegram — os comandos em si são
    // tratados pelo ChatBotProcessor (Application), que funciona em qualquer canal.
    private static readonly BotCommand[] ComandosPadrao =
    [
        new() { Command = "ajuda", Description = "Mostra os comandos disponíveis" },
        new() { Command = "sobre", Description = "Sobre este bot" },
        new() { Command = "limpar", Description = "Apaga o histórico da conversa e recomeça" },
    ];

    // "fb:" + id (bigint, até 19 dígitos) + ":" + voto ("1" ou "-1") fica bem abaixo dos
    // 64 bytes que o Telegram permite em callback_data — folga enorme mesmo em cenários extremos.
    private const string PrefixoCallbackFeedback = "fb:";

    private readonly ITelegramBotClient _bot;
    private readonly IBotComandosService _comandos;
    private readonly IConversaHistoricoService _historicoService;
    private readonly ILogger<TelegramChatChannel> _logger;

    public TelegramChatChannel(
        IOptions<TelegramChatChannelOptions> options,
        IBotComandosService comandos,
        IConversaHistoricoService historicoService,
        ILogger<TelegramChatChannel> logger)
    {
        _bot = new TelegramBotClient(options.Value.BotToken);
        _comandos = comandos;
        _historicoService = historicoService;
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

                if (update.CallbackQuery is { Data: not null } callback)
                {
                    // Fluxo à parte do de mensagens normais — tratado inteiramente aqui dentro
                    // do canal, não vira IncomingMessage (o Domain/Application não precisam
                    // saber que um voto de feedback é, no Telegram, um callback de botão).
                    await TratarCallbackFeedbackAsync(callback, ct);
                    continue;
                }

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

        // Respostas de comando (ajuda, sobre, etc.) não são "avaliáveis" — não fazem parte
        // do fluxo de pergunta/resposta do RAG, então não recebem botões de feedback.
        await ResponderAsync(chatId, resposta, ct: ct);
        return true;
    }

    public async Task IndicarProcessandoAsync(string chatId, CancellationToken ct = default)
    {
        try
        {
            await _bot.SendChatAction(long.Parse(chatId), ChatAction.Typing, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Não foi possível enviar indicador de digitação para {ChatId}", chatId);
        }
    }

    public async Task ResponderAsync(string chatId, string texto, string? avaliacaoId = null, CancellationToken ct = default)
    {
        var id = long.Parse(chatId);
        var partes = DividirEmPartes(texto, LimiteSeguroPorMensagem);

        // Divide ANTES de converter pra HTML — cada parte vira uma mensagem própria, então
        // dividir por parágrafo aqui evita cortar uma tag <b>/<i> ao meio entre duas mensagens.
        for (var i = 0; i < partes.Count; i++)
        {
            // O agente escreve em markdown simples, sem saber que o canal é o Telegram —
            // a conversão para o formato de exibição é responsabilidade deste canal.
            var html = TelegramFormatador.ParaHtml(partes[i]);

            // Só a ÚLTIMA parte leva os botões — não faz sentido pedir feedback em cada
            // pedaço de uma resposta longa dividida em várias mensagens.
            var ehUltimaParte = i == partes.Count - 1;
            var teclado = ehUltimaParte ? CriarTecladoFeedback(avaliacaoId) : null;

            try
            {
                await _bot.SendMessage(id, html, parseMode: ParseMode.Html, replyMarkup: teclado, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                // HTML do Telegram só aceita um subconjunto de tags e não perdoa marcação malformada —
                // se o texto gerado quebrar o parser, reenvia sem formatação a perder a resposta.
                _logger.LogWarning(ex, "Falha ao enviar mensagem formatada (HTML) para {ChatId} — reenviando como texto simples", chatId);
                var textoSimples = TagHtml.Replace(html, string.Empty);
                await _bot.SendMessage(id, textoSimples, replyMarkup: teclado, cancellationToken: ct);
            }
        }
    }

    private static InlineKeyboardMarkup? CriarTecladoFeedback(string? avaliacaoId)
    {
        if (avaliacaoId is null) return null;

        return new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("👍 Útil", $"{PrefixoCallbackFeedback}{avaliacaoId}:1"),
                InlineKeyboardButton.WithCallbackData("👎 Não útil", $"{PrefixoCallbackFeedback}{avaliacaoId}:-1"),
            ]
        ]);
    }

    /// <summary>
    /// Trata o clique nos botões de feedback (👍/👎). Sempre responde ao callback (mesmo em
    /// erro) para o Telegram parar de mostrar o "relógio girando" no botão do usuário.
    /// </summary>
    private async Task TratarCallbackFeedbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var data = callback.Data!;
        if (!data.StartsWith(PrefixoCallbackFeedback, StringComparison.Ordinal))
            return;

        var partes = data[PrefixoCallbackFeedback.Length..].Split(':');
        if (partes.Length != 2)
        {
            _logger.LogWarning("callback_data de feedback em formato inesperado: {Data}", data);
            return;
        }

        var conversaId = partes[0];
        var util = partes[1] == "1";

        try
        {
            // conversaId pode vir composto (ex.: com um id de rastreamento externo embutido) —
            // isso é decisão de quem persiste o histórico, não do canal. Ver
            // FeedbackExternoConversaHistoricoService.
            await _historicoService.RegistrarFeedbackAsync(conversaId, util, ct);
            await _bot.AnswerCallbackQuery(callback.Id, "Valeu pelo feedback!", cancellationToken: ct);

            // Remove os botões pra não deixar votar de novo na mesma resposta — não é crítico
            // (RegistrarFeedbackAsync apenas sobrescreve o voto), então falha aqui é só logada.
            if (callback.Message is not null)
            {
                await _bot.EditMessageReplyMarkup(callback.Message.Chat.Id, callback.Message.MessageId, replyMarkup: null, cancellationToken: ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Falha ao registrar feedback do callback {Data}", data);
        }
    }

    /// <summary>
    /// Corta o texto em pedaços que cabem numa mensagem do Telegram, preferindo cortar
    /// em quebra de parágrafo (linha em branco) — só corta no meio de um parágrafo se ele
    /// sozinho já for maior que o limite.
    /// </summary>
    private static List<string> DividirEmPartes(string texto, int limite)
    {
        if (texto.Length <= limite) return [texto];

        var partes = new List<string>();
        var atual = new StringBuilder();

        foreach (var paragrafo in texto.Split("\n\n"))
        {
            var candidato = atual.Length == 0 ? paragrafo : $"{atual}\n\n{paragrafo}";

            if (candidato.Length > limite && atual.Length > 0)
            {
                partes.Add(atual.ToString());
                atual.Clear();
                atual.Append(paragrafo);
            }
            else
            {
                atual.Clear();
                atual.Append(candidato);
            }

            while (atual.Length > limite)
            {
                partes.Add(atual.ToString(0, limite));
                atual.Remove(0, limite);
            }
        }

        if (atual.Length > 0) partes.Add(atual.ToString());
        return partes;
    }
}
