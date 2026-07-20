using System.Collections.Concurrent;
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
    IBuscaSemanticaService embeddingService,
    IAgentService agentService,
    IConversaHistoricoService historicoService,
    ITelemetriaService telemetriaService,
    ITracingService tracingService,
    IGuardRailPipeline guardRailPipeline,
    IContextoTelemetria contextoTelemetria,
    IOptions<RagOptions> ragOptions,
    IOptions<DebugMetricasOptions> debugOptions,
    IOptions<HistoricoOptions> historicoOptions,
    IOptions<BotOptions> botOptions,
    ILogger<ChatBotProcessor> logger)
{
    // Uma entrada por chat com processamento em andamento — permite cancelar a resposta
    // anterior quando uma pergunta nova chega antes dela terminar (ver RunAsync).
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _processandoPorChat = new();

    public async Task RunAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Bot escutando mensagens...");

        await foreach (var mensagem in channel.OuvirAsync(ct))
        {
            // Se o mesmo chat já tem uma pergunta em processamento, cancela — o usuário mandou
            // outra pergunta antes da resposta anterior terminar, então ela não serve mais pra
            // ele. Não garante economia de tokens se o cancelamento chegar tarde demais (a
            // chamada ao Claude já pode ter sido faturada do lado de lá mesmo sem o cliente
            // receber a resposta), mas evita gastar RAG + geração + envio numa resposta que
            // ninguém vai ler, e evita duas respostas concorrentes pro mesmo chat se pisando
            // no histórico. Processar cada chat como uma Task própria (em vez de aguardar
            // sequencialmente aqui) também deixa chats diferentes andarem em paralelo.
            if (_processandoPorChat.TryRemove(mensagem.ChatId, out var anterior))
                await anterior.CancelAsync();

            var ctChat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _processandoPorChat[mensagem.ChatId] = ctChat;

            _ = ProcessarMensagemAsync(mensagem, ctChat.Token);
        }
    }

    /// <summary>Internal (não private) só pra ser testável diretamente — ver Giom.VideoManager.Application.Tests.</summary>
    internal async Task ProcessarMensagemAsync(IncomingMessage mensagem, CancellationToken ct)
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
        using var trace = tracingService.IniciarTrace("bot.interacao", "bot.responder");

        // Vira o EntidadeId do telemetria "bot_interacao" assim que existir — identifica a
        // pergunta/resposta específica (conversa_historico.id), não só o chat. Só fica null
        // se a exceção acontecer antes do AdicionarAsync (ex.: falha na busca RAG), quando
        // ainda não existe uma linha de conversa pra apontar.
        TurnoConversa? turno = null;

        try
        {
            logger.LogInformation("Pergunta recebida de {ChatId} ({Usuario}): {Pergunta}", mensagem.ChatId, mensagem.UserName, mensagem.Texto);

            var contextoGuardRail = new GuardRailContexto(mensagem.ChatId, mensagem.Texto);
            GuardRailResultado resultadoEntrada;
            using (trace.IniciarEtapa("guardrail_entrada"))
            {
                resultadoEntrada = await guardRailPipeline.AvaliarEntradaAsync(contextoGuardRail, ct);
            }

            if (!resultadoEntrada.Permitido)
            {
                indicadorCts.Cancel();
                await channel.ResponderAsync(mensagem.ChatId, resultadoEntrada.RespostaSubstituta ?? "Não posso responder essa pergunta.", ct: ct);

                await telemetriaService.RegistrarAsync(new RegistroTelemetria(
                    Tipo: "guardrail_bloqueio",
                    EntidadeId: mensagem.ChatId,
                    TempoMs: stopwatch.ElapsedMilliseconds,
                    Sucesso: true,
                    Detalhes: new { fase = "entrada", motivo = resultadoEntrada.Motivo }
                ), ct);

                return;
            }

            // Grava a pergunta antes de qualquer outra coisa (mesmo antes da busca RAG) —
            // se algo falhar logo em seguida, a pergunta já fica registrada. O turno (com
            // Id) também vira o EntidadeId de toda telemetria desta interação, inclusive a
            // da busca RAG abaixo.
            turno = await historicoService.AdicionarAsync(mensagem.ChatId, new TurnoConversa(null, mensagem.Texto, null), ct);

            var historico = AplicarLimiteFifo(await historicoService.ObterAsync(mensagem.ChatId, ct), historicoOptions.Value.LimiteCaracteres, mensagem.ChatId);

            var buscaStopwatch = System.Diagnostics.Stopwatch.StartNew();
            BuscaSemanticaResultado busca;
            var buscaOk = true;
            try
            {
                using (trace.IniciarEtapa("busca_rag"))
                {
                    busca = await embeddingService.BuscarAsync(
                        mensagem.Texto, canalId: ragOptions.Value.CanalId, limite: ragOptions.Value.Limite, threshold: ragOptions.Value.Threshold, ct: ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Degrada em vez de derrubar o turno inteiro: sem contexto de aula o agente
                // ainda consegue responder (avisando que não achou a aula), o que é melhor do
                // que a pergunta ficar sem resposta nenhuma por uma falha isolada da busca.
                buscaOk = false;
                trace.MarcarErro(ex);
                logger.LogError(ex, "Busca RAG falhou para {ChatId} — seguindo sem contexto de aula", mensagem.ChatId);
                busca = new BuscaSemanticaResultado([], "Nenhuma aula relevante encontrada.");
            }

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

            await telemetriaService.RegistrarAsync(new RegistroTelemetria(
                Tipo: "busca_rag",
                EntidadeId: turno.Id!.Value.ToString(),
                TempoMs: buscaStopwatch.ElapsedMilliseconds,
                Sucesso: buscaOk,
                Detalhes: new
                {
                    chunks = busca.Chunks.Count,
                    similaridadeMin = busca.Chunks.Count > 0 ? busca.Chunks.Min(c => c.Similarity) : (double?)null,
                    similaridadeMax = busca.Chunks.Count > 0 ? busca.Chunks.Max(c => c.Similarity) : (double?)null,
                    videos = busca.Chunks.Select(c => c.Titulo).Distinct(),
                    canalId = ragOptions.Value.CanalId
                }
            ), ct);

            // Aberto ANTES de chamar o agente (não dentro dele) pra que o TelemetriaAgentService,
            // decorator que envolve IAgentService, reaproveite este mesmo escopo em vez de abrir
            // um novo — só assim os tokens/custo que ele coleta ficam visíveis aqui embaixo.
            var metaAgente = contextoTelemetria.IniciarEscopo();

            RespostaAgente respostaAgente;
            using (trace.IniciarEtapa("agente"))
            {
                respostaAgente = await agentService.ResponderAsync(mensagem.ChatId, turno, busca.ContextoFormatado, historico, ct);
            }

            var respostaTexto = respostaAgente.Texto;
            using (trace.IniciarEtapa("guardrail_saida"))
            {
                var resultadoSaida = await guardRailPipeline.AvaliarSaidaAsync(contextoGuardRail with { Resposta = respostaTexto }, ct);
                if (!resultadoSaida.Permitido)
                    respostaTexto = resultadoSaida.RespostaSubstituta ?? respostaTexto;
            }

            // ReasoningContent (US #126) — só provedores com "thinking" separado do texto final
            // (Kimi K3) preenchem RespostaAgente.ReasoningContent; pra Claude/Grok/OpenAI vem
            // sempre null, e o turno é gravado igual sempre foi.
            turno = turno with { Resposta = respostaTexto, ReasoningContent = respostaAgente.ReasoningContent };
            await historicoService.AtualizarAsync(mensagem.ChatId, turno, ct);

            indicadorCts.Cancel();
            // O Id vira o "avaliacaoId" repassado ao canal — é o que liga o voto de 👍/👎
            // do usuário de volta a esta pergunta/resposta específica. Quando existe um
            // RequestIdExterno (ex.: Helicone), vai embutido no mesmo avaliacaoId — evita
            // persistir uma coluna nova só pra isso (o canal decompõe na hora do voto).
            var avaliacaoId = metaAgente.RequestIdExterno is { } requestIdExterno
                ? $"{turno.Id}|{requestIdExterno}"
                : turno.Id.ToString();

            using (trace.IniciarEtapa("envio"))
            {
                await channel.ResponderAsync(mensagem.ChatId, respostaTexto, avaliacaoId, ct);
            }
            logger.LogInformation("Respondido: {ChatId} — {Usuario} ({TempoMs}ms)", mensagem.ChatId, mensagem.UserName, stopwatch.ElapsedMilliseconds);

            if (debugOptions.Value.Habilitado)
            {
                await channel.ResponderAsync(mensagem.ChatId, FormatarDebugMetricas(metaAgente, stopwatch.ElapsedMilliseconds, debugOptions.Value), ct: ct);
            }

            // Tempo entre a pergunta e a resposta enviada, do ponto de vista do aluno —
            // inclui busca RAG + geração + envio, não só a chamada ao Claude isoladamente
            // (essa já é medida à parte pelo decorator TelemetriaAgentService).
            await telemetriaService.RegistrarAsync(new RegistroTelemetria(
                Tipo: "bot_interacao",
                EntidadeId: turno.Id!.Value.ToString(),
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: true
            ), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelado porque o mesmo chat mandou uma pergunta nova (ver RunAsync) — não é
            // erro, é o comportamento esperado. Não registra "bot_interacao" com Sucesso=false
            // pra não poluir a métrica de erro com algo que não é falha nenhuma.
            indicadorCts.Cancel();
            logger.LogInformation("Processamento de {ChatId} cancelado — chegou uma pergunta nova antes da resposta anterior terminar", mensagem.ChatId);
        }
        catch (Exception ex)
        {
            indicadorCts.Cancel();
            trace.MarcarErro(ex);
            logger.LogError(ex, "Erro ao processar mensagem de {ChatId}", mensagem.ChatId);

            // Antes do AdicionarAsync (ex.: falha na busca RAG) ainda não existe
            // conversa_historico pra apontar — cai de volta pro chatId nesse caso.
            await telemetriaService.RegistrarAsync(new RegistroTelemetria(
                Tipo: "bot_interacao",
                EntidadeId: turno?.Id?.ToString() ?? mensagem.ChatId,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: false
            ), ct);

            // Sem isso, uma falha em qualquer etapa (busca RAG, agente, etc.) deixava o aluno
            // sem resposta nenhuma — silêncio total, pior ainda numa pergunta sensível. Usa
            // CancellationToken.None: ct já pode estar cancelado (é o mesmo token do try acima),
            // e essa mensagem de erro deve sair mesmo assim.
            try
            {
                await channel.ResponderAsync(mensagem.ChatId, botOptions.Value.MensagemErro, ct: CancellationToken.None);
            }
            catch (Exception erroEnvio)
            {
                logger.LogError(erroEnvio, "Falha ao enviar mensagem de erro pra {ChatId}", mensagem.ChatId);
            }
        }
        finally
        {
            await indicadorTask;
            _processandoPorChat.TryRemove(mensagem.ChatId, out _);
        }
    }

    /// <summary>
    /// Descarta os turnos mais ANTIGOS primeiro (FIFO) até o histórico caber no limite de
    /// caracteres configurado — mesmo espírito do que o Claude.ai/Claude Code fazem quando a
    /// conversa cresce demais, só que sem custo de uma chamada extra pra "compactar": aqui é
    /// puro corte, sem resumir o que foi descartado. Sempre mantém pelo menos o turno mais
    /// recente, mesmo que ele sozinho já ultrapasse o limite.
    /// </summary>
    private IReadOnlyList<TurnoConversa> AplicarLimiteFifo(IReadOnlyList<TurnoConversa> historico, int limiteCaracteres, string chatId)
    {
        var mantidos = new List<TurnoConversa>();
        var total = 0;

        for (var i = historico.Count - 1; i >= 0; i--)
        {
            var turno = historico[i];
            // ReasoningContent (US #126) entra no orçamento — é conteúdo real que o Kimi manda
            // de volta na próxima chamada, não metadado inerte. Fica 0 pra turnos de
            // Claude/Grok/OpenAI (ReasoningContent sempre null).
            var tamanho = turno.Pergunta.Length + (turno.Resposta?.Length ?? 0) + (turno.ReasoningContent?.Length ?? 0);
            if (mantidos.Count > 0 && total + tamanho > limiteCaracteres)
                break;

            mantidos.Add(turno);
            total += tamanho;
        }

        mantidos.Reverse();

        var descartados = historico.Count - mantidos.Count;
        if (descartados > 0)
            logger.LogInformation("Histórico truncado (FIFO): {Descartados} turno(s) mais antigo(s) removido(s) do contexto de {ChatId}", descartados, chatId);

        return mantidos;
    }

    /// <summary>
    /// Mensagem de acompanhamento só pra Dev (ver DebugMetricasOptions) — tokens/tempo/custo
    /// coletados pelo TelemetriaAgentService via escopo reaproveitado (ver IniciarEscopo acima).
    /// </summary>
    private static string FormatarDebugMetricas(MetadadosTelemetria meta, long tempoMs, DebugMetricasOptions opcoes)
    {
        var tokensEntrada = meta.TokensEntrada ?? 0;
        var tokensSaida = meta.TokensSaida ?? 0;
        var tokensTotal = tokensEntrada + tokensSaida;
        var pctJanela = opcoes.ContextWindowTokens > 0 ? (double)tokensTotal / opcoes.ContextWindowTokens : 0;

        return $"""
            🔧 Debug
            Tokens: {tokensEntrada} entrada + {tokensSaida} saída = {tokensTotal}
            Tempo: {tempoMs}ms
            Custo estimado: {(meta.CustoEstimadoUsd is { } custo ? $"${custo:F6}" : "n/d")}
            Janela de contexto: {FormatarBarraProgresso(pctJanela)} {pctJanela:P1} ({tokensTotal:N0} / {opcoes.ContextWindowTokens:N0})
            """;
    }

    /// <summary>Barra de progresso em texto (Telegram não tem elemento visual nativo pra isso).</summary>
    private static string FormatarBarraProgresso(double percentual, int largura = 20)
    {
        var preenchido = Math.Clamp((int)Math.Round(percentual * largura), 0, largura);
        return $"[{new string('█', preenchido)}{new string('░', largura - preenchido)}]";
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
