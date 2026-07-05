namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Agent de perguntas e respostas. Não conhece canal (Telegram, WhatsApp, web) nem faz
/// busca semântica — recebe a pergunta e o contexto já recuperado (RAG é responsabilidade
/// de quem orquestra, não do agente), devolve a resposta gerada.
/// </summary>
public interface IAgentService
{
    Task<RespostaAgente> ResponderAsync(string pergunta, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default);
}

/// <summary>
/// Resposta do agente + metadados de telemetria. Campos além de Texto são nullable —
/// nem todo provedor tem noção de "modelo" ou "tokens" (ver ITelemetriaService).
/// </summary>
public record RespostaAgente(
    string Texto,
    string? Provedor = null,
    string? Modelo = null,
    int? TokensEntrada = null,
    int? TokensSaida = null);
