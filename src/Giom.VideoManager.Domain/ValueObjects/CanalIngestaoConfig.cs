namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Um canal de ingestão, lido do banco (tabela canal_ingestao) em vez de hardcoded em
/// Program.cs — ver US #105. <see cref="SecretsKey"/> não é o segredo em si, só aponta pro path
/// no SSM onde vive a credencial Google desse canal (segredo nunca sai do SSM).
/// <see cref="Regra"/> (#180) é a regra de relevância do canal — quais vídeos entram no pipeline —,
/// vinda de canal_ingestao.regra_relevancia.
/// </summary>
public record CanalIngestaoConfig(
    string ChannelId,
    string? PlaylistId,
    string Nome,
    bool Mine,
    bool Ativo,
    RegraRelevancia Regra,
    int MaxVideosPerRun,
    int DelayBetweenVideosMs,
    int DelayBetweenPagesMs,
    string SecretsKey);
