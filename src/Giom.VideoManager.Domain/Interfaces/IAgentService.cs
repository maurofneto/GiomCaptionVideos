namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Agent de perguntas e respostas. Não conhece canal (Telegram, WhatsApp, web) nem faz
/// busca semântica — recebe a pergunta e o contexto já recuperado (RAG é responsabilidade
/// de quem orquestra, não do agente), devolve a resposta gerada.
/// Metadados de telemetria (provedor/modelo/tokens) não fazem parte do retorno — quem
/// quiser contribuí-los usa IContextoTelemetria, lido pelo decorator TelemetriaAgentService.
/// turnoAtual carrega o Id do turno (já gravado por quem orquestra, com a Resposta ainda
/// null) — usado pela telemetria pra identificar exatamente qual pergunta/resposta gerou
/// aquela chamada, em vez de só o chat.
/// </summary>
public interface IAgentService
{
    Task<RespostaAgente> ResponderAsync(string chatId, TurnoConversa turnoAtual, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default);
}

/// <summary>
/// Resposta de um IAgentService. ReasoningContent (US #126) só vem preenchido de provedores com
/// "thinking" separado do texto final (Kimi K3) — a API deles exige reenviar esse raciocínio nas
/// próximas chamadas da MESMA conversa pra manter o histórico coerente. Claude/Grok/OpenAI
/// sempre devolvem null aqui; quem consome (ChatBotProcessor) grava direto em
/// TurnoConversa.ReasoningContent, sem lógica condicional por provedor.
/// </summary>
public record RespostaAgente(string Texto, string? ReasoningContent = null);
