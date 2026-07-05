namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Agent de perguntas e respostas. Não conhece canal (Telegram, WhatsApp, web) nem faz
/// busca semântica — recebe a pergunta e o contexto já recuperado (RAG é responsabilidade
/// de quem orquestra, não do agente), devolve a resposta gerada.
/// Metadados de telemetria (provedor/modelo/tokens) não fazem parte do retorno — quem
/// quiser contribuí-los usa IContextoTelemetria, lido pelo decorator TelemetriaAgentService.
/// </summary>
public interface IAgentService
{
    Task<string> ResponderAsync(string chatId, string pergunta, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default);
}
