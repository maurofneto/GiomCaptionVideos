using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Anthropic;

/// <summary>
/// Estende a sessão de canal/página/vídeo já aberta pelo ChannelProcessor com o segmento da
/// etapa atual (resumo/categorização), em vez de recomeçar uma sessão isolada por vídeo — é
/// isso que dá a hierarquia canal->página->vídeo->etapa na plataforma de observabilidade
/// externa (Helicone). Se não houver sessão de canal aberta (ex.: chamada avulsa em teste),
/// cai de volta pra uma sessão isolada por vídeo.
/// </summary>
internal static class SessaoPipelineExtensions
{
    public static SessaoObservabilidade ParaEtapa(this SessaoObservabilidade? sessaoBase, string etapa, Video video)
    {
        var propriedades = sessaoBase?.Propriedades is not null
            ? new Dictionary<string, string>(sessaoBase.Propriedades)
            : new Dictionary<string, string>();
        propriedades["Tipo"] = etapa;

        return new SessaoObservabilidade(
            SessionId: sessaoBase?.SessionId ?? video.Id,
            SessionName: sessaoBase?.SessionName ?? video.Title,
            SessionPath: $"{sessaoBase?.SessionPath ?? "/pipeline"}/{etapa}",
            UserId: sessaoBase?.UserId,
            Propriedades: propriedades);
    }
}
