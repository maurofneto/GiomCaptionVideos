using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Orquestra o fluxo de chat: escuta o canal, faz a busca semântica (RAG) e delega
/// ao agente apenas a geração da resposta. A busca é feita aqui — não no agente —
/// para que qualquer implementação de IAgentService (Claude ou outra) receba o
/// mesmo contexto, sem duplicar a lógica de recuperação em cada adapter.
/// </summary>
public class ChatBotProcessor(
    IChatChannel channel,
    IEmbeddingService embeddingService,
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
                var busca = await embeddingService.BuscarAsync(mensagem.Texto, ct: ct);
                var resposta = await agentService.ResponderAsync(mensagem.Texto, busca.ContextoFormatado, ct);
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
