namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Mantém o histórico recente de conversa por chat, para que o agente possa se referir
/// ao que ele mesmo já respondeu. Sem isso, cada mensagem é tratada como uma pergunta
/// isolada e o agente não consegue explicar ou reconciliar suas próprias respostas anteriores.
/// </summary>
public interface IConversaHistoricoService
{
    Task<IReadOnlyList<TurnoConversa>> ObterAsync(string chatId, CancellationToken ct = default);

    Task AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default);

    /// <summary>Apaga todo o histórico de um chat — usado pelo comando /limpar.</summary>
    Task LimparAsync(string chatId, CancellationToken ct = default);
}

/// <summary>Um par pergunta/resposta já concluído na conversa.</summary>
public record TurnoConversa(string Pergunta, string Resposta);
