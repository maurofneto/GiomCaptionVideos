using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Feedback;

/// <summary>
/// Decorator genérico (mesmo padrão dos decorators de telemetria): qualquer IConversaHistoricoService
/// passa a reenviar o voto de utilidade (👍/👎) pra um serviço de feedback externo (ex.: Helicone),
/// sem que o canal (Telegram, etc.) precise saber que isso existe. O canal só vê "avaliacaoId" —
/// se ele vier composto como "{conversaId}|{requestIdExterno}" (ver ChatBotProcessor), decompomos
/// aqui: a parte pura vai pro inner (persistência normal), a segunda parte (se houver) vai pro
/// IFeedbackExternoService. Sem "|" no avaliacaoId, comportamento idêntico a hoje.
/// </summary>
public class FeedbackExternoConversaHistoricoService(IConversaHistoricoService inner, IFeedbackExternoService feedbackExterno) : IConversaHistoricoService
{
    public Task<IReadOnlyList<TurnoConversa>> ObterAsync(string chatId, CancellationToken ct = default)
        => inner.ObterAsync(chatId, ct);

    public Task<TurnoConversa> AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
        => inner.AdicionarAsync(chatId, turno, ct);

    public Task AtualizarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
        => inner.AtualizarAsync(chatId, turno, ct);

    public Task LimparAsync(string chatId, CancellationToken ct = default)
        => inner.LimparAsync(chatId, ct);

    public async Task RegistrarFeedbackAsync(string conversaId, bool util, CancellationToken ct = default)
    {
        var (idPuro, requestIdExterno) = conversaId.Split('|') switch
        {
            [var id, var reqId] => (id, (string?)reqId),
            _ => (conversaId, null)
        };

        await inner.RegistrarFeedbackAsync(idPuro, util, ct);

        if (requestIdExterno is not null)
            await feedbackExterno.EnviarFeedbackAsync(requestIdExterno, util, ct);
    }
}
