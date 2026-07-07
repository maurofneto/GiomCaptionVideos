using System.Net.Http.Headers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Supabase;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Substitui a busca real no YouTube por vídeos que já existem no Supabase (dev) — conteúdo real
/// (título/canal de verdade), mas devolvidos com um Id novo a cada execução, pra InstitutoVideoHandler
/// nunca encontrar um "já processado" (GetByIdAsync não acha nada com esse Id) e sempre gerar
/// resumo/categorização/embedding de novo. O Id REAL de origem (de onde a legenda será buscada)
/// viaja em Video.PlaylistId — ver SupabaseCaptionService, que lê esse campo pra saber qual
/// transcricao buscar. Só pega vídeos que já têm transcricao (join inner) — sem isso não haveria
/// conteúdo pra alimentar o resumo.
/// </summary>
public class SupabaseVideoSource : IVideoSourceService
{
    private readonly HttpClient _http;

    public SupabaseVideoSource(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory)
    {
        var opcoes = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
        _http.BaseAddress = new Uri(opcoes.Url);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opcoes.SecretKey);
        _http.DefaultRequestHeaders.Add("apikey", opcoes.SecretKey);
    }

    public Task<string> GetUploadPlaylistIdAsync(ChannelConfig config, CancellationToken ct = default)
        => Task.FromResult("supabase-playlist-teste");

    public async Task<PlaylistPage> GetPlaylistPageAsync(string playlistId, int maxResults, string? pageToken, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"/rest/v1/video?select=id,title,channel_title&transcricao!inner(video_id)&order=processed_at.desc.nullslast&limit={maxResults}", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var linhas = JsonConvert.DeserializeObject<List<VideoRowDto>>(json) ?? [];

        var agora = DateTime.UtcNow;
        var videos = linhas.Select((linha, i) => new Video
        {
            Id = $"teste-{Guid.NewGuid():N}"[..12],
            Title = linha.title ?? "(sem título)",
            Description = $"Vídeo de teste de observabilidade — conteúdo emprestado do Supabase dev (origem: {linha.id}).",
            ChannelId = "supabase-instituto-teste",
            ChannelTitle = linha.channel_title ?? "Instituto Giom (Teste)",
            PlaylistId = linha.id, // id REAL de origem — SupabaseCaptionService busca a transcricao por ele
            Type = VideoType.Live,
            PrivacyStatus = "unlisted",
            UploadedAt = agora.AddMinutes(-i)
        }).ToList();

        return new PlaylistPage(videos, NextPageToken: null);
    }

    private record VideoRowDto(string id, string? title, string? channel_title);
}
