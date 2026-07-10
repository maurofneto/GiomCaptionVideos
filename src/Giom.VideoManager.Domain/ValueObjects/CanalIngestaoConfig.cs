namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Regra de relevância de um canal, como dado (não código) — ver US #105. "TituloContem" cobre
/// tanto Contains quanto StartsWith: um valor terminando em "*" em
/// <see cref="CanalIngestaoConfig.RelevanciaValores"/> é tratado como prefixo, sem "*" como
/// substring. Evita um 3º tipo de enum só pra essa mistura.
/// </summary>
public enum RelevanciaTipo
{
    LiveUnlisted,
    TituloContem
}

/// <summary>
/// Um canal de ingestão, lido do banco (tabela canal_ingestao) em vez de hardcoded em
/// Program.cs — ver US #105. <see cref="SecretsKey"/> não é o segredo em si, só aponta pro path
/// no SSM onde vive a credencial Google desse canal (segredo nunca sai do SSM).
/// </summary>
public record CanalIngestaoConfig(
    string ChannelId,
    string? PlaylistId,
    string Nome,
    bool Mine,
    bool Ativo,
    RelevanciaTipo RelevanciaTipo,
    IReadOnlyList<string> RelevanciaValores,
    int MaxVideosPerRun,
    int DelayBetweenVideosMs,
    int DelayBetweenPagesMs,
    string SecretsKey);
