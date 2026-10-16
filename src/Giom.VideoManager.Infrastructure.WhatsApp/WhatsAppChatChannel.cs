using System.Net.Http.Headers;
using System.Text;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Canais;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.WhatsApp;

/// <summary>
/// Canal de chat via WhatsApp Cloud API (Meta), oficial e sem BSP — ver Spike #138.
///
/// Diferenças relevantes em relação ao TelegramChatChannel, todas absorvidas aqui dentro pra
/// que Application/Domain sigam iguais:
/// <list type="bullet">
/// <item>ENTRADA é webhook (o Meta empurra), não polling — OuvirAsync só consome a fila que o
/// endpoint HTTP alimenta (ver <see cref="IWhatsAppInbox"/>).</item>
/// <item>NÃO implementa IndicarProcessandoAsync (fica o no-op padrão da interface): o indicador
/// de "digitando" do WhatsApp exige o id da mensagem recebida, e a assinatura do Domain só
/// entrega o chatId. Preferi deixar sem do que inventar estado escondido só pra isso — a
/// interface já prevê canal sem esse recurso.</item>
/// <item>Feedback (👍/👎) vai numa mensagem interativa SEPARADA, não anexada à resposta: o
/// corpo de uma mensagem interativa do WhatsApp é limitado a 1024 caracteres, bem menos que os
/// 4096 de uma mensagem de texto. Anexar os botões na última parte amarraria o tamanho da
/// resposta ao limite menor.</item>
/// </list>
/// </summary>
public class WhatsAppChatChannel : IChatChannel
{
    /// <summary>Chave deste canal no container (US #24) — o bot sobe um ChatBotProcessor por canal ligado.</summary>
    public const string ChaveCanal = "whatsapp";

    // Limite real do corpo de texto é 4096; a margem cobre o que a conversão pra marcação do
    // WhatsApp pode acrescentar (cada ** vira *, mas ``` cresce em relação a `).
    private const int LimiteSeguroPorMensagem = 3800;

    internal const string PrefixoBotaoFeedback = "fb:";

    private readonly HttpClient _http;
    private readonly IWhatsAppInbox _inbox;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppChatChannel> _logger;

    public WhatsAppChatChannel(
        IHttpClientFactory httpClientFactory,
        IWhatsAppInbox inbox,
        IOptions<WhatsAppOptions> options,
        ILogger<WhatsAppChatChannel> logger)
    {
        _inbox = inbox;
        _options = options.Value;
        _logger = logger;

        _http = httpClientFactory.CreateClient("whatsapp");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
    }

    public IAsyncEnumerable<IncomingMessage> OuvirAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Canal WhatsApp pronto — aguardando mensagens entregues pelo webhook");
        return _inbox.LerAsync(ct);
    }

    public async Task ResponderAsync(string chatId, string texto, string? avaliacaoId = null, CancellationToken ct = default)
    {
        // Divide ANTES de converter, mesma ordem do Telegram: assim a marcação de uma parte
        // nunca fica com a abertura numa mensagem e o fechamento na seguinte.
        foreach (var parte in DivisorDeMensagem.DividirEmPartes(texto, LimiteSeguroPorMensagem))
        {
            await EnviarAsync(new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = chatId,
                type = "text",
                text = new { preview_url = false, body = WhatsAppFormatador.ParaWhatsApp(parte) }
            }, chatId, ct);
        }

        if (avaliacaoId is not null)
            await EnviarBotoesFeedbackAsync(chatId, avaliacaoId, ct);
    }

    /// <summary>
    /// Mensagem curta e separada só com os dois botões. Falha aqui é logada e engolida de
    /// propósito: a resposta em si já chegou ao aluno, e perder o botão de feedback é bem menos
    /// grave do que estourar uma exceção que faria o ChatBotProcessor mandar mensagem de erro
    /// por cima de uma resposta que deu certo.
    /// </summary>
    private async Task EnviarBotoesFeedbackAsync(string chatId, string avaliacaoId, CancellationToken ct)
    {
        try
        {
            await EnviarAsync(new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = chatId,
                type = "interactive",
                interactive = new
                {
                    type = "button",
                    body = new { text = "Essa resposta te ajudou?" },
                    action = new
                    {
                        buttons = new object[]
                        {
                            // title do botão é limitado a 20 caracteres e o id a 256 — os dois
                            // cabem com folga aqui (o id carrega no máximo turnoId + request
                            // externo, ver ChatBotProcessor.avaliacaoId).
                            new { type = "reply", reply = new { id = $"{PrefixoBotaoFeedback}{avaliacaoId}:1", title = "👍 Útil" } },
                            new { type = "reply", reply = new { id = $"{PrefixoBotaoFeedback}{avaliacaoId}:-1", title = "👎 Não útil" } }
                        }
                    }
                }
            }, chatId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Resposta entregue, mas falhou o envio dos botões de feedback para {ChatId}", chatId);
        }
    }

    private async Task EnviarAsync(object payload, string chatId, CancellationToken ct)
    {
        var conteudo = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        var resposta = await _http.PostAsync($"{_options.ApiVersion}/{_options.PhoneNumberId}/messages", conteudo, ct);

        if (!resposta.IsSuccessStatusCode)
        {
            var corpo = await resposta.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"WhatsApp: envio para {chatId} falhou: {resposta.StatusCode} — {corpo}", inner: null, statusCode: resposta.StatusCode);
        }
    }
}
