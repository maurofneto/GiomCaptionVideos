namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Mantém o histórico recente de conversa por chat, para que o agente possa se referir
/// ao que ele mesmo já respondeu. Sem isso, cada mensagem é tratada como uma pergunta
/// isolada e o agente não consegue explicar ou reconciliar suas próprias respostas anteriores.
/// </summary>
public interface IConversaHistoricoService
{
    Task<IReadOnlyList<TurnoConversa>> ObterAsync(string chatId, CancellationToken ct = default);

    /// <summary>
    /// Insere o turno (Resposta normalmente ainda null nesse ponto — grava só a pergunta pra
    /// já existir um Id antes de chamar o agente) e devolve o mesmo turno com o Id preenchido.
    /// </summary>
    Task<TurnoConversa> AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default);

    /// <summary>Atualiza um turno já existente (ex: preenche a Resposta depois que o agente responde) — localizado por turno.Id.</summary>
    Task AtualizarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default);

    /// <summary>
    /// Reinicia a conversa de um chat pro agente — usado pelo comando /limpar. Não apaga o
    /// histórico anterior (fica preservado pra auditoria/depuração), só faz o agente parar
    /// de enxergá-lo: as próximas mensagens começam do zero, como uma nova conversa.
    /// </summary>
    Task LimparAsync(string chatId, CancellationToken ct = default);

    /// <summary>Registra o voto de utilidade (👍/👎) dado pelo usuário para uma resposta já enviada.</summary>
    Task RegistrarFeedbackAsync(string conversaId, bool util, CancellationToken ct = default);
}

/// <summary>
/// Um turno de pergunta/resposta na conversa. Id e Resposta ficam nulos entre o momento em que
/// a pergunta é gravada (AdicionarAsync) e o momento em que a resposta do agente é conhecida
/// (AtualizarAsync) — isso permite repassar o turno (com Id) pro agente ANTES da resposta
/// existir, para que a telemetria do agente já identifique a qual turno ela pertence.
/// </summary>
public record TurnoConversa(long? Id, string Pergunta, string? Resposta);
