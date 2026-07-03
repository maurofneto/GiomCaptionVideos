namespace Domain.Interfaces;

/// <summary>
/// Agent de perguntas e respostas com RAG. Não conhece canal (Telegram, WhatsApp, web) —
/// recebe texto, devolve texto.
/// </summary>
public interface IAgentService
{
    Task<string> PerguntarAsync(string pergunta, string? area = null, CancellationToken ct = default);
}
