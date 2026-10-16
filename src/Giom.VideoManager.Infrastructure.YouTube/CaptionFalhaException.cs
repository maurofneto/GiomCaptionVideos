using Giom.VideoManager.Domain.Exceptions;

namespace Giom.VideoManager.Infrastructure.YouTube;

/// <summary>
/// Falha de legenda já classificada. Subclasse de <see cref="NenhumResultadoEncontradoException"/>:
/// o <c>FallbackHandler</c> continua tratando como "passa pro próximo elo", exatamente como antes.
/// Quota segue como <see cref="RateLimitExcedidoException"/> e credencial do canal como
/// <see cref="CredencialCanalIndisponivelException"/> — não passam por este tipo.
/// </summary>
public class CaptionFalhaException(
    ClasseFalhaCaption classe, string origem, string motivo,
    int? httpStatus = null, string? reason = null, Exception? innerException = null)
    : NenhumResultadoEncontradoException(origem, motivo, innerException)
{
    public ClasseFalhaCaption Classe { get; } = classe;
    public int? HttpStatus { get; } = httpStatus;
    public string? Reason { get; } = reason;
}
