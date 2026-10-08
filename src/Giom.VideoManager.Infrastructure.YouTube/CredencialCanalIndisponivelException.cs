namespace Giom.VideoManager.Infrastructure.YouTube;

/// <summary>
/// A credencial OAuth Google do canal não está disponível (ausente, vazia ou recusada pelo
/// Google no boot). Fica no adapter YouTube — nenhum consumidor precisa ramificar por este tipo:
/// o <c>FallbackHandler</c> trata qualquer exceção como "passa pro próximo elo". O
/// <see cref="Motivo"/> é um resumo sem segredo (ex: <c>invalid_grant</c>, <c>RefreshToken ausente</c>).
/// </summary>
public class CredencialCanalIndisponivelException(string channelId, string motivo, Exception? innerException = null)
    : Exception($"Credencial Google do canal {channelId} indisponível: {motivo}", innerException)
{
    public string ChannelId { get; } = channelId;
    public string Motivo { get; } = motivo;
}
