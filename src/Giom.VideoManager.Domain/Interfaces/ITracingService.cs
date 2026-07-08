namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Rastreamento de uma operação com múltiplas etapas (ex: busca RAG -> chamada ao agente ->
/// envio da resposta), para visualizar onde o tempo está indo dentro de uma interação. A
/// implementação concreta decide pra onde isso vai (ex: Sentry Performance) — quem orquestra
/// (ChatBotProcessor) só sabe que existe um trace com etapas nomeadas.
/// </summary>
public interface ITracingService
{
    /// <summary>
    /// contextoExterno (opcional) — traceparent W3C (ver CapturarContextoAtual) de um trace já
    /// encerrado, pra reatar a árvore quando o próximo nível só existe depois de um hop
    /// assíncrono (ex.: fila de ingestão — US #39/#40): sem isso, o trace.Current ambient já
    /// voltou a null pelo tempo que o consumer roda, e o novo Span vira raiz isolada em vez de
    /// filho. Sem contextoExterno, comportamento idêntico a antes (usa Activity.Current ambient).
    /// </summary>
    ITrace IniciarTrace(string nome, string operacao, string? contextoExterno = null);

    /// <summary>
    /// Abre uma etapa dentro do trace ambiente atual (o que estiver "corrente" no momento —
    /// ver ITrace), sem precisar receber o ITrace por parâmetro. Existe pra quem está deep numa
    /// chamada (ex.: ClaudeResumoService, TelemetriaCaptionService) poder abrir seu próprio Span
    /// sem que o chamador (ex.: ChannelProcessor) precise passar o trace adiante manualmente.
    /// Sem trace ambiente aberto, vira só um objeto vazio (não lança, não gera efeito).
    /// </summary>
    IDisposable IniciarEtapaAtual(string nome);

    /// <summary>
    /// Devolve o traceparent W3C (ex.: "00-{traceId}-{spanId}-{flags}") do trace/etapa ambiente
    /// atual, ou null se não houver nenhum aberto — pra levar junto numa mensagem de fila e
    /// reatar a árvore do outro lado (ver IniciarTrace, parâmetro contextoExterno).
    /// </summary>
    string? CapturarContextoAtual();
}

/// <summary>Uma operação completa sendo rastreada — Dispose finaliza o trace inteiro.</summary>
public interface ITrace : IDisposable
{
    /// <summary>Inicia uma etapa — Dispose do retorno finaliza só essa etapa.</summary>
    IDisposable IniciarEtapa(string nome);

    /// <summary>Marca o trace como falho — chamar em vez de deixar o Dispose finalizar normalmente.</summary>
    void MarcarErro(Exception ex);

    /// <summary>Anexa uma tag pra diferenciar este trace de outros do mesmo nome (ex.: video.id, pagina.token) — pesquisável no provider concreto.</summary>
    void DefinirTag(string chave, string valor);
}
