namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Rastreamento de uma operação com múltiplas etapas (ex: busca RAG -> chamada ao agente ->
/// envio da resposta), para visualizar onde o tempo está indo dentro de uma interação. A
/// implementação concreta decide pra onde isso vai (ex: Sentry Performance) — quem orquestra
/// (ChatBotProcessor) só sabe que existe um trace com etapas nomeadas.
/// </summary>
public interface ITracingService
{
    ITrace IniciarTrace(string nome, string operacao);
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
