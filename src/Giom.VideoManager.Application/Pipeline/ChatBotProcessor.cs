using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// Telemetria do agente (tempo/modelo/tokens da chamada ao Claude em si) é responsabilidade
/// do decorator TelemetriaAgentService que envolve IAgentService. Aqui registramos só o
/// round-trip completo do ponto de vista do aluno (busca RAG + geração + envio), que é uma
/// métrica própria da orquestração — não de uma implementação específica de IAgentService.
/// </summary>
public class ChatBotProcessor(
    IChatChannel channel,
    IEmbeddingService embeddingService,
    IAgentService agentService,
    IConversaHistoricoService historicoService,
    ITelemetriaService telemetriaService,
    IOptions<RagOptions> ragOptions,
    ILogger<ChatBotProcessor> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Bot escutando mensagens...");

        await foreach (var mensagem in channel.OuvirAsync(ct))
        {
            // CorrelationId + ChatId via BeginScope (abstração padrão do .NET, não Serilog
            // direto) — o provider de log (Serilog) injeta esses valores em TODAS as linhas
            // emitidas durante o processamento desta mensagem, mesmo as de outras classes
            // (ClaudeAgentService, SupabaseEmbeddingService, etc.), permitindo rastrear uma
            // conversa inteira nos logs mesmo espalhada entre componentes diferentes.
            var correlationId = Guid.NewGuid().ToString("N")[..8];
            using var escopo = logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["ChatId"] = mensagem.ChatId
            });

            using var indicadorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var indicadorTask = ManterIndicandoProcessandoAsync(mensagem.ChatId, indicadorCts.Token);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                logger.LogInformation("Pergunta recebida de {ChatId} ({Usuario}): {Pergunta}", mensagem.ChatId, mensagem.UserName, mensagem.Texto);

                var historico = await historicoService.ObterAsync(mensagem.ChatId, ct);
                var busca = await embeddingService.BuscarAsync(
                    mensagem.Texto, canalId: ragOptions.Value.CanalId, limite: ragOptions.Value.Limite, threshold: ragOptions.Value.Threshold, ct: ct);

                if (busca.Chunks.Count == 0)
                {
                    logger.LogWarning("Busca RAG sem resultados para {ChatId} (threshold {Threshold})", mensagem.ChatId, ragOptions.Value.Threshold);
                }
                else
                {
                    logger.LogInformation(
                        "Busca RAG: {Qtd} chunks de [{Videos}] — similaridade {Min:P0}-{Max:P0}",
                        busca.Chunks.Count,
                        string.Join(", ", busca.Chunks.Select(c => c.Titulo).Distinct()),
                        busca.Chunks.Min(c => c.Similarity),
                        busca.Chunks.Max(c => c.Similarity));
                }

                var respostaTexto = await agentService.ResponderAsync(mensagem.ChatId, mensagem.Texto, busca.ContextoFormatado, historico, ct);

                // O id retornado vira o "avaliacaoId" repassado ao canal — é o que liga o
                // voto de 👍/👎 do usuário de volta a esta pergunta/resposta específica.
                var conversaId = await historicoService.AdicionarAsync(mensagem.ChatId, new TurnoConversa(mensagem.Texto, respostaTexto), ct);

                indicadorCts.Cancel();
                await channel.ResponderAsync(mensagem.ChatId, respostaTexto, conversaId.ToString(), ct);
                logger.LogInformation("Respondido: {ChatId} — {Usuario} ({TempoMs}ms)", mensagem.ChatId, mensagem.UserName, stopwatch.ElapsedMilliseconds);

                // Tempo entre a pergunta e a resposta enviada, do ponto de vista do aluno —
                // inclui busca RAG + geração + envio, não só a chamada ao Claude isoladamente
                // (essa já é medida à parte pelo decorator TelemetriaAgentService).
                await telemetriaService.RegistrarAsync(new RegistroTelemetria(
                    Tipo: "bot_interacao",
                    EntidadeId: mensagem.ChatId,
                    TempoMs: stopwatch.ElapsedMilliseconds,
                    Sucesso: true
                ), ct);
            }
            catch (Exception ex)
            {
                indicadorCts.Cancel();
                logger.LogError(ex, "Erro ao processar mensagem de {ChatId}", mensagem.ChatId);

                await telemetriaService.RegistrarAsync(new RegistroTelemetria(
                    Tipo: "bot_interacao",
                    EntidadeId: mensagem.ChatId,
                    TempoMs: stopwatch.ElapsedMilliseconds,
                    Sucesso: false
                ), ct);
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
