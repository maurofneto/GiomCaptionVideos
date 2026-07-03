using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Orquestra o fluxo de chat: escuta o canal, faz a busca semântica (RAG) e delega
/// ao agente apenas a geração da resposta. A busca é feita aqui — não no agente —
/// para que qualquer implementação de IAgentService (Claude ou outra) receba o
/// mesmo contexto, sem duplicar a lógica de recuperação em cada adapter.
/// Mantém o histórico por chat para que o agente possa se referir ao que ele mesmo
/// já respondeu — sem isso, cada pergunta é tratada isoladamente.
/// Comandos utilitários (ajuda, sobre, limpar) não passam por aqui — cada canal os
/// reconhece do seu próprio jeito e chama IBotComandosService diretamente (ver TelegramChatChannel).
/// </summary>
public class ChatBotProcessor(
    IChatChannel channel,
    IEmbeddingService embeddingService,
    IAgentService agentService,
    IConversaHistoricoService historicoService,
    ILogger<ChatBotProcessor> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Bot escutando mensagens...");

        await foreach (var mensagem in channel.OuvirAsync(ct))
        {
            using var indicadorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var indicadorTask = ManterIndicandoProcessandoAsync(mensagem.ChatId, indicadorCts.Token);

            try
            {
                var historico = await historicoService.ObterAsync(mensagem.ChatId, ct);
                var busca = await embeddingService.BuscarAsync(mensagem.Texto, ct: ct);
                var resposta = await agentService.ResponderAsync(mensagem.Texto, busca.ContextoFormatado, historico, ct);
                await historicoService.AdicionarAsync(mensagem.ChatId, new TurnoConversa(mensagem.Texto, resposta), ct);

                indicadorCts.Cancel();
                await channel.ResponderAsync(mensagem.ChatId, resposta, ct);
                logger.LogInformation("Respondido: {ChatId} — {Usuario}", mensagem.ChatId, mensagem.UserName);
            }
            catch (Exception ex)
            {
                indicadorCts.Cancel();
                logger.LogError(ex, "Erro ao processar mensagem de {ChatId}", mensagem.ChatId);
            }
            finally
            {
                await indicadorTask;
            }
        }
    }

    /// <summary>
    /// O indicador de "digitando" do Telegram expira a cada ~5s — refresca em loop
    /// enquanto a busca+geração da resposta estiver em andamento.
    /// </summary>
    private async Task ManterIndicandoProcessandoAsync(string chatId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await channel.IndicarProcessandoAsync(chatId, ct);
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // esperado ao cancelar assim que a resposta fica pronta
        }
    }
}
