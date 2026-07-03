namespace Giom.VideoManager.Application.Options;

public class ProcessingOptions
{
    public int MaxVideosPerRun { get; init; } = 10;
    public bool ShouldGetCaption { get; init; } = true;
    public bool ShouldTranscribe { get; init; } = false;
    public bool ShouldGenerateSummary { get; init; } = false;
    public bool ShouldCategorize { get; init; } = false;
    public bool ShouldIndexEmbedding { get; init; } = false;

    /// <summary>Delay em ms entre páginas de playlist para respeitar rate limit.</summary>
    public int DelayBetweenPagesMs { get; init; } = 1200;

    /// <summary>Delay em ms entre vídeos.</summary>
    public int DelayBetweenVideosMs { get; init; } = 3000;
}
