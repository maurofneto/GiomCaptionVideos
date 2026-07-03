using Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Application.Pipeline;

public class ChatBotProcessor(
    IChatChannel channel,
    IAgentService agentService,
    ILogger<ChatBotProcessor> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Bot escutando mensagens...");

        await foreach (var mensagem in channel.OuvirAsync(ct))
        {
            try
            {
                var resposta = await agentService.PerguntarAsync(mensagem.Texto, ct: ct);
                await channel.ResponderAsync(mensagem.ChatId, resposta, ct);
                logger.LogInformation("Respondido: {ChatId} — {Usuario}", mensagem.ChatId, mensagem.UserName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao processar mensagem de {ChatId}", mensagem.ChatId);
            }
        }
    }
}
