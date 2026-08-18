using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Giom.VideoManager.Infrastructure.WhatsApp;

/// <summary>
/// Traduz o payload cru que o Meta entrega no webhook para o vocabulário do Domain.
///
/// Mora neste projeto (e não no host HTTP da task #140) de propósito: o formato do JSON do
/// WhatsApp é conhecimento de protocolo do canal, exatamente o tipo de coisa que a arquitetura
/// hexagonal manda manter dentro do adapter. O endpoint HTTP fica sendo só encanamento — rota,
/// desafio de verificação, assinatura — e chama <see cref="ProcessarAsync"/> com o corpo do POST.
///
/// Espelha o que o TelegramChatChannel faz dentro do laço de polling: separa clique de botão de
/// feedback, comando e pergunta normal, e só a pergunta vira IncomingMessage pro Application.
/// </summary>
public class WhatsAppWebhookProcessor
{
    private readonly WhatsAppChatChannel _canal;
    private readonly IWhatsAppInbox _inbox;
    private readonly IBotComandosService _comandos;
    private readonly IConversaHistoricoService _historicoService;
    private readonly ILogger<WhatsAppWebhookProcessor> _logger;
    private readonly MensagensJaProcessadas _jaProcessadas = new();

    public WhatsAppWebhookProcessor(
        WhatsAppChatChannel canal,
        IWhatsAppInbox inbox,
        IBotComandosService comandos,
        IConversaHistoricoService historicoService,
        ILogger<WhatsAppWebhookProcessor> logger)
    {
        _canal = canal;
        _inbox = inbox;
        _comandos = comandos;
        _historicoService = historicoService;
        _logger = logger;
    }

    /// <summary>
    /// Nunca lança por causa do conteúdo do payload: o Meta reentrega tudo que não responder
    /// 200, então estourar exceção numa mensagem malformada viraria reentrega infinita da mesma
    /// mensagem ruim. Erro de formato é logado e descartado.
    /// </summary>
    public async Task ProcessarAsync(string corpoJson, CancellationToken ct = default)
    {
        JObject raiz;
        try
        {
            raiz = JObject.Parse(corpoJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payload do webhook do WhatsApp não é JSON válido — descartando");
            return;
        }

        // entry[].changes[].value — qualquer nível pode faltar (ex.: notificação de status de
        // entrega, que vem em "statuses" dentro do mesmo "value" e não interessa aqui).
        var blocos = raiz["entry"]?
            .SelectMany(e => e["changes"] ?? Enumerable.Empty<JToken>())
            .Select(c => c["value"])
            .OfType<JToken>()
            .ToList() ?? [];

        foreach (var bloco in blocos)
            foreach (var mensagem in bloco["messages"] ?? Enumerable.Empty<JToken>())
                await ProcessarMensagemAsync(mensagem, bloco, ct);
    }

    private async Task ProcessarMensagemAsync(JToken mensagem, JToken bloco, CancellationToken ct)
    {
        var chatId = mensagem["from"]?.Value<string>();
        var mensagemId = mensagem["id"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(chatId)) return;

        // O Meta reentrega qualquer webhook que não responda 200 a tempo — sem esta trava, uma
        // reentrega viraria outra chamada ao agente e outra cobrança de LLM pela MESMA pergunta.
        if (mensagemId is not null && !_jaProcessadas.RegistrarSeNova(mensagemId))
        {
            _logger.LogInformation("Mensagem {MensagemId} do WhatsApp já processada — reentrega do Meta ignorada", mensagemId);
            return;
        }

        var tipo = mensagem["type"]?.Value<string>();

        if (tipo == "interactive")
        {
            await TratarBotaoFeedbackAsync(mensagem, ct);
            return;
        }

        if (tipo != "text")
        {
            _logger.LogInformation("Mensagem do tipo {Tipo} recebida de {ChatId} — canal só entende texto", tipo, chatId);
            await _canal.ResponderAsync(chatId, "Por enquanto eu só consigo ler mensagens de texto. Me manda sua pergunta escrita?", ct: ct);
            return;
        }

        var texto = mensagem["text"]?["body"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(texto)) return;

        if (await TratarComandoAsync(chatId, texto, ct)) return;

        await _inbox.PublicarAsync(new IncomingMessage(chatId, texto, ObterNome(bloco)), ct);
    }

    /// <summary>
    /// O WhatsApp não marca comando como entidade própria (o Telegram marca) — aqui a única
    /// convenção possível é o texto começar com "/", igual o aluno já usa no Telegram.
    /// </summary>
    internal async Task<bool> TratarComandoAsync(string chatId, string texto, CancellationToken ct)
    {
        var primeira = texto.TrimStart().Split(' ')[0];
        if (!primeira.StartsWith('/')) return false;

        var resposta = primeira.ToLowerInvariant() switch
        {
            "/start" => _comandos.ObterBoasVindas(),
            "/ajuda" or "/help" => _comandos.ObterAjuda(),
            "/sobre" => _comandos.ObterSobre(),
            "/limpar" or "/reset" => await _comandos.LimparAsync(chatId, ct),
            _ => $"Não conheço o comando {primeira}. Manda /ajuda pra ver o que eu sei fazer."
        };

        // Resposta de comando não entra no fluxo de RAG, então não é avaliável — sem botões.
        await _canal.ResponderAsync(chatId, resposta, ct: ct);
        return true;
    }

    internal async Task TratarBotaoFeedbackAsync(JToken mensagem, CancellationToken ct)
    {
        var id = mensagem["interactive"]?["button_reply"]?["id"]?.Value<string>();
        if (id is null || !id.StartsWith(WhatsAppChatChannel.PrefixoBotaoFeedback, StringComparison.Ordinal)) return;

        var partes = id[WhatsAppChatChannel.PrefixoBotaoFeedback.Length..].Split(':');
        if (partes.Length != 2)
        {
            _logger.LogWarning("Id de botão de feedback em formato inesperado: {Id}", id);
            return;
        }

        try
        {
            // Igual ao Telegram: o id da conversa pode vir composto (com rastreamento externo
            // embutido) — quem persiste o histórico é que sabe decompor, não o canal.
            await _historicoService.RegistrarFeedbackAsync(partes[0], partes[1] == "1", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Falha ao registrar feedback do botão {Id}", id);
        }
    }

    /// <summary>O nome de exibição não vem na mensagem, vem no bloco "contacts" ao lado dela.</summary>
    private static string? ObterNome(JToken bloco)
        => bloco["contacts"]?.FirstOrDefault()?["profile"]?["name"]?.Value<string>();
}
