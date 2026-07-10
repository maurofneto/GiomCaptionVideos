namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Os 3 parâmetros opcionais no fim (null por padrão) são overrides por canal — ver US #105 e
/// CanalIngestaoConfig. Quando null, ChannelProcessor usa o valor global de ProcessingOptions;
/// quando setado (lido de canal_ingestao), sobrescreve só pra esse canal — preserva a variação
/// que já existia hoje (ex: Instituto MaxVideosPerRun=20, Ministério=35) sem exigir um
/// ProcessingOptions por worker.
/// </summary>
public record ChannelConfig(
    string ChannelId,
    string? PlaylistId,
    string Name,
    bool Mine = false,
    int? MaxVideosPerRun = null,
    int? DelayBetweenVideosMs = null,
    int? DelayBetweenPagesMs = null
);
