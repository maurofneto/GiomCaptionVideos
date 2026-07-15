using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.SmokeTests;

/// <summary>
/// Substitui a busca real no YouTube por vídeos 100% sintéticos — sem nenhuma leitura no
/// Supabase pra "emprestar" conteúdo (era a fonte de toda a dor de cabeça: sintaxe de multi-join
/// do PostgREST, pool de vídeos se esgotando/poluindo entre runs, mistura acidental prod/dev).
/// A PERSISTÊNCIA continua indo pro Supabase de dev de verdade (via AddPersistence) — é isso que
/// valida span/telemetria/Helicone ponta a ponta, o propósito real desta ferramenta (Board #96).
/// Só o CONTEÚDO de entrada (legenda/resumo/categoria/embedding) não depende mais de dado real.
/// US #106 — canaisDeTeste (Program.cs) usa os ChannelId REAIS do Instituto/Ministério (não mais
/// "fake-instituto-teste"/"fake-ministerio-teste"): etapa_provedor_config tem FK pra
/// canal_ingestao(channel_id), só assim a config por canal (prompt/UsarObservabilidade) resolve
/// de verdade — o vídeo em si continua 100% sintético, só o ChannelId é real.
/// </summary>
public class FakeVideoSource : IVideoSourceService
{
    public const string ChannelIdInstituto = "UCyyXFx2xHdzfavx6H6Q8zJw";
    public const string ChannelIdMinisterio = "UCyHMyZH5oon8jW7y_yMRstQ";

    private static readonly string[] TitulosInstituto =
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

    // US #105 — inclui de propósito 2 títulos que NÃO contêm "CULTO"/"GIOM"/"MINISTÉRIO" (a
    // regra TituloContem do canal Ministério de teste), pra provar que o filtro de relevância
    // realmente descarta algo, não deixa tudo passar. Cuidado: a explicação NÃO pode citar essas
    // palavras dentro do próprio título, senão o teste "passa" por acidente (bug real que
    // aconteceu aqui na primeira versão).
    private static readonly string[] TitulosMinisterio =
    [
        "Culto de Domingo | Ceia do Senhor",
        "GIOM ao vivo — Multiplicando Discípulos",
        "Ministério em Foco | Ação Social",
        "Aula de Teologia Sistemática", // propositalmente irrelevante — não contém nenhuma palavra-chave
        "Anúncios da semana", // propositalmente irrelevante — não contém nenhuma palavra-chave
    ];

    /// <summary>Um playlist id por canal (derivado do ChannelId) — permite GetPlaylistPageAsync saber qual conjunto de vídeos sintéticos gerar, já que só recebe o playlistId, não o ChannelConfig inteiro.</summary>
    public Task<string> GetUploadPlaylistIdAsync(ChannelConfig config, CancellationToken ct = default)
        => Task.FromResult($"fake-playlist-{config.ChannelId}");

    public Task<PlaylistPage> GetPlaylistPageAsync(string playlistId, int maxResults, string? pageToken, CancellationToken ct = default)
    {
        var channelId = playlistId["fake-playlist-".Length..];
        var ehMinisterio = channelId == ChannelIdMinisterio;
        var titulos = ehMinisterio ? TitulosMinisterio : TitulosInstituto;
        var channelTitle = ehMinisterio ? "Ministério Giom" : "Instituto Giom";

        var agora = DateTime.UtcNow;
        var videos = Enumerable.Range(0, maxResults).Select(i => new Video
        {
            Id = $"teste-{Guid.NewGuid():N}"[..12],
            Title = titulos[i % titulos.Length] + (i >= titulos.Length ? $" ({i / titulos.Length + 1})" : ""),
            Description = "Vídeo sintético de teste E2E — sem YouTube, sem IA.",
            ChannelId = channelId,
            ChannelTitle = channelTitle,
            PlaylistId = playlistId,
            // Ministério não usa LiveUnlisted como regra de relevância (usa TituloContem), então
            // Type/PrivacyStatus não precisam simular "ao vivo" pra ele — refletem o formato mais
            // comum de verdade (uploads normais).
            Type = ehMinisterio ? VideoType.Upload : VideoType.Live,
            PrivacyStatus = ehMinisterio ? "public" : "unlisted",
            UploadedAt = agora.AddMinutes(-i)
        }).ToList();

        return Task.FromResult(new PlaylistPage(videos, NextPageToken: null));
    }
}
