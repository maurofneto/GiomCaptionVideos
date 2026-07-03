namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Agent de perguntas e respostas. Não conhece canal (Telegram, WhatsApp, web) nem faz
/// busca semântica — recebe a pergunta e o contexto já recuperado (RAG é responsabilidade
/// de quem orquestra, não do agente), devolve a resposta gerada.
/// </summary>
public interface IAgentService
{
    Task<string> ResponderAsync(string pergunta, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default);
}
