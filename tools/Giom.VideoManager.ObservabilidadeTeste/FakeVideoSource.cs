using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Substitui a busca real no YouTube por vídeos 100% sintéticos — sem nenhuma leitura no
/// Supabase pra "emprestar" conteúdo (era a fonte de toda a dor de cabeça: sintaxe de multi-join
/// do PostgREST, pool de vídeos se esgotando/poluindo entre runs, mistura acidental prod/dev).
/// A PERSISTÊNCIA continua indo pro Supabase de dev de verdade (via AddPersistence) — é isso que
/// valida span/telemetria/Helicone ponta a ponta, o propósito real desta ferramenta (Board #96).
/// Só o CONTEÚDO de entrada (legenda/resumo/categoria/embedding) não depende mais de dado real.
/// </summary>
public class FakeVideoSource : IVideoSourceService
{
    private static readonly string[] Titulos =
    [
        "GIOM | Ceia do Senhor | Domingo | 18h",
        "GIOM | O Poder da Oração | Quarta | 20h",
        "GIOM | Casa de Pedro | Domingo | 10h",
        "GIOM | Multiplicando Discípulos | Quarta | 20h",
        "GIOM | Culto Legacy | Somos Filhos | Domingo | 18h",
        "GIOM | Quando Deus entra em cena | Domingo | 10h",
        "GIOM | Casa de Mateus | Domingo | 18h",
        "GIOM | Deus tem uma virada para você | Domingo | 10h",
    ];

    public Task<string> GetUploadPlaylistIdAsync(ChannelConfig config, CancellationToken ct = default)
        => Task.FromResult("fake-playlist-teste");

    public Task<PlaylistPage> GetPlaylistPageAsync(string playlistId, int maxResults, string? pageToken, CancellationToken ct = default)
    {
        var agora = DateTime.UtcNow;
        var videos = Enumerable.Range(0, maxResults).Select(i => new Video
        {
            Id = $"teste-{Guid.NewGuid():N}"[..12],
            Title = Titulos[i % Titulos.Length] + (i >= Titulos.Length ? $" ({i / Titulos.Length + 1})" : ""),
            Description = "Vídeo sintético de teste E2E — sem YouTube, sem IA.",
            ChannelId = "fake-instituto-teste",
            ChannelTitle = "Instituto Giom (Teste E2E)",
            PlaylistId = playlistId,
            Type = VideoType.Live,
            PrivacyStatus = "unlisted",
            UploadedAt = agora.AddMinutes(-i)
        }).ToList();

        return Task.FromResult(new PlaylistPage(videos, NextPageToken: null));
    }
}
