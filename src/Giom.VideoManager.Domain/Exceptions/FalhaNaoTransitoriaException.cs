namespace Giom.VideoManager.Domain.Exceptions;

/// <summary>
/// Provedor externo recusou de um jeito que repetir a mesma chamada minutos depois não resolve
/// (ex.: YouTube exigindo "Sign in to confirm you're not a bot" pro yt-dlp — só muda trocando
/// cookies/PO token/IP). Subclasse de <see cref="NenhumResultadoEncontradoException"/>: a cadeia
/// de fallback continua passando pro próximo elo normalmente; quem muda é o consumer de fila,
/// que manda a mensagem direto pra DLQ em vez de queimar as N tentativas do MaxReceiveCount —
/// sem a fila precisar conhecer YouTube/yt-dlp (mesmo papel de <see cref="RateLimitExcedidoException"/>).
/// </summary>
public class FalhaNaoTransitoriaException(string origem, string motivo, string? reason = null, Exception? innerException = null)
    : NenhumResultadoEncontradoException(origem, motivo, innerException)
{
    /// <summary>Código curto e estável da causa (ex.: <c>sign_in_to_confirm_not_a_bot</c>) — vai pra log/tag, nunca texto de terceiros.</summary>
    public string? Reason { get; } = reason;
}
