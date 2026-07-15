using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.Application.Handlers;

/// <summary>
/// Único IVideoHandler pra qualquer canal (US #105) — substitui InstitutoPipelineHandler/
/// MinisterioPipelineHandler, que já eram idênticos em HandleAsync, só IsRelevant divergia. A
/// regra de relevância agora é dado (<see cref="CanalIngestaoConfig.RelevanciaTipo"/>), não
/// código — zero conhecimento de "Instituto" ou "Ministério" aqui. Construído manualmente (não
/// resolvido via DI) uma vez por canal lido do banco, ver AddIngestaoMultiCanal na Composition —
/// não dá pra ser um singleton comum, a quantidade de instâncias só se sabe em runtime.
/// </summary>
public class CanalIngestaoVideoHandler(
    CanalIngestaoConfig canal,
    IFilaPublisher<VideoEtapaMensagem> transcricaoPublisher,
    ISessionContext sessionContext,
    ITracingService tracingService)
    : IVideoHandler
{
    public bool IsRelevant(Video video) => canal.RelevanciaTipo switch
    {
        RelevanciaTipo.LiveUnlisted => video.Type == VideoType.Live && video.PrivacyStatus == "unlisted",
        RelevanciaTipo.TituloContem => AvaliarTitulo(video.Title.ToUpperInvariant(), canal.RelevanciaValores),
        _ => throw new InvalidOperationException($"RelevanciaTipo não suportado: {canal.RelevanciaTipo}")
    };

    /// <summary>Valor terminando em "*" = prefixo (StartsWith), sem "*" = substring (Contains) — ver comentário em RelevanciaTipo.</summary>
    private static bool AvaliarTitulo(string tituloEmMaiusculas, IReadOnlyList<string> valores) => valores.Any(valor =>
        valor.EndsWith('*')
            ? tituloEmMaiusculas.StartsWith(valor[..^1].ToUpperInvariant())
            : tituloEmMaiusculas.Contains(valor.ToUpperInvariant()));

    public Task HandleAsync(Video video, CancellationToken ct = default) =>
        transcricaoPublisher.PublicarAsync(new VideoEtapaMensagem(video, sessionContext.Atual, tracingService.CapturarContextoAtual()), ct);
}
