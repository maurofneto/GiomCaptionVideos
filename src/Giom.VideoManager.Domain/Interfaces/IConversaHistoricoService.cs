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
    /// Retorna o id do registro criado — usado pelo canal para atrelar o voto de feedback
    /// (ex: callback_data do botão do Telegram) a esta pergunta/resposta específica.
    /// </summary>
    Task<long> AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default);

    /// <summary>
    /// Reinicia a conversa de um chat pro agente — usado pelo comando /limpar. Não apaga o
    /// histórico anterior (fica preservado pra auditoria/depuração), só faz o agente parar
    /// de enxergá-lo: as próximas mensagens começam do zero, como uma nova conversa.
    /// </summary>
    Task LimparAsync(string chatId, CancellationToken ct = default);

    /// <summary>Registra o voto de utilidade (👍/👎) dado pelo usuário para uma resposta já enviada.</summary>
    Task RegistrarFeedbackAsync(string conversaId, bool util, CancellationToken ct = default);
}

/// <summary>Um par pergunta/resposta já concluído na conversa.</summary>
public record TurnoConversa(string Pergunta, string Resposta);
