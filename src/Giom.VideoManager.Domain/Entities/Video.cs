using Giom.VideoManager.Domain.Enums;

namespace Giom.VideoManager.Domain.Entities;

public class Video
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string ChannelTitle { get; set; } = string.Empty;
    public string PlaylistId { get; set; } = string.Empty;
    public string Length { get; set; } = string.Empty;
    public string PrivacyStatus { get; set; } = string.Empty;
    public VideoType Type { get; set; } = VideoType.Upload;
    public DateTime? UploadedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }

    public Transcricao? Transcricao { get; set; }
    public Resumo? Resumo { get; set; }
    public IList<CategoriaTeologica> Categorias { get; set; } = [];
    public IList<VideoChunk> Chunks { get; set; } = [];
}
