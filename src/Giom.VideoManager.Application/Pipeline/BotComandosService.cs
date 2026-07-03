using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Implementa as capacidades utilitárias do bot (IBotComandosService). Não sabe nada sobre
/// canal — quem decide como e quando chamar cada método é o adapter de canal (ex: TelegramChatChannel
/// reconhecendo a entidade BotCommand do Telegram e mapeando pro método correspondente).
/// </summary>
public class BotComandosService(
    IConversaHistoricoService historicoService,
    IOptions<BotOptions> botOptions,
    ILogger<BotComandosService> logger) : IBotComandosService
{
    private const string TextoAjuda = """
        Posso:
        - mostrar esta ajuda (ajuda)
        - contar sobre mim (sobre)
        - apagar o histórico desta conversa e recomeçar do zero (limpar)
        """;

    public string ObterBoasVindas() => botOptions.Value.MensagemBoasVindas;

    public string ObterAjuda() => TextoAjuda;

    public string ObterSobre() => botOptions.Value.MensagemSobre;

    public async Task<string> LimparAsync(string chatId, CancellationToken ct = default)
    {
        await historicoService.LimparAsync(chatId, ct);
        logger.LogInformation("Histórico limpo para {ChatId}", chatId);
        return botOptions.Value.MensagemLimpar;
    }
}
