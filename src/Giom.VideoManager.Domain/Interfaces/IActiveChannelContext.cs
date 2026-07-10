namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Canal (AsyncLocal, mesmo padrão de ISessionContext) sendo processado na cadeia de await
/// atual — ver US #105. Existe pra YouTubeVideoSourceService saber qual YouTubeService keyed
/// usar quando um único worker processa N canais numa run só (cada canal pode ter credencial
/// Google diferente). ChannelProcessor define o canal ativo no início de cada
/// ProcessChannelAsync; como é AsyncLocal, flui por toda a paginação/vídeos daquele canal sem
/// precisar ser resetado manualmente a cada chamada.
/// </summary>
public interface IActiveChannelContext
{
    void Definir(string channelId);

    string? Atual { get; }
}
