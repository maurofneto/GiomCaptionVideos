namespace Giom.VideoManager.Infrastructure.YouTube;

/// <summary>
/// Motivo (sem segredo) pelo qual a credencial de cada canal falhou no boot — preenchido por
/// <c>AddYouTubeCredenciaisPorCanal</c>, lido por <see cref="YouTubeCaptionService"/> pra montar
/// a <see cref="CredencialCanalIndisponivelException"/> com a causa real.
/// </summary>
public sealed class FalhasCredencialPorCanal(IReadOnlyDictionary<string, string> motivos)
{
    public bool TentarObterMotivo(string channelId, out string motivo)
    {
        if (motivos.TryGetValue(channelId, out var m))
        {
            motivo = m;
            return true;
        }

        motivo = string.Empty;
        return false;
    }
}
