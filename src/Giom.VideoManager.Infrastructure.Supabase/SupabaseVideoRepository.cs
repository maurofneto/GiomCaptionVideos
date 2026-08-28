using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Net.Http.Headers;
using System.Text;

namespace Giom.VideoManager.Infrastructure.Supabase;

public class SupabaseOptions
{
    public string Url { get; init; } = string.Empty;
    public string PublishableKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;

    /// <summary>
    /// Cloud: "/rest/v1". PostgREST no Railway: "".
    /// </summary>
    public string RestPrefix { get; init; } = "/rest/v1";

    /// <summary>
    /// Cloud: true (Bearer + apikey). Railway sem PGRST_JWT_SECRET: false, senão PGRST300.
    /// </summary>
    public bool SendAuthHeaders { get; init; } = true;

    public string Rest(string pathAndQuery)
    {
        var prefix = string.IsNullOrWhiteSpace(RestPrefix) ? "" : RestPrefix.TrimEnd('/');
        var path = pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery;
        return prefix + path;
    }
}

/// <summary>
/// Repositório de vídeos usando Supabase REST API (PostgREST).
/// </summary>
public class SupabaseVideoRepository : IVideoRepository
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;
    private readonly ILogger<SupabaseVideoRepository> _logger;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        NullValueHandling = NullValueHandling.Ignore
    };

    public SupabaseVideoRepository(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory, ILogger<SupabaseVideoRepository> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = httpClientFactory.CreateClient("supabase");
    }

    public async Task<Video?> GetByIdAsync(string videoId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(_options.Rest($"/video?id=eq.{videoId}&select=*,transcricao(*),resumo(*,capitulo(*)),categoria_teologica(video_id,area,subcategoria,peso),video_chunk(chunk_index,origem)"), ct);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        var list = JsonConvert.DeserializeObject<List<VideoDto>>(json, JsonSettings);
        return list?.FirstOrDefault()?.ToDomain();
    }

    public async Task UpsertAsync(Video video, CancellationToken ct = default)
    {
        await PostgRestUpsertAsync(_options.Rest("/video?on_conflict=id"), VideoDto.From(video), ct);
        if (video.Transcricao != null) await PostgRestUpsertAsync(_options.Rest("/transcricao?on_conflict=video_id"), TranscricaoDto.From(video.Transcricao), ct);
        if (video.Resumo != null) await PostgRestUpsertAsync(_options.Rest("/resumo?on_conflict=video_id"), ResumoDto.From(video.Resumo), ct);
        if (video.Categorias.Count > 0) await UpsertCategoriasAsync(video.Categorias, ct);
    }

    private async Task UpsertCategoriasAsync(IList<CategoriaTeologica> categorias, CancellationToken ct)
    {
        // Apaga as categorias antigas do vídeo e insere as novas em batch
        var videoId = categorias[0].VideoId;
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, _options.Rest($"/categoria_teologica?video_id=eq.{videoId}"));
        var deleteResponse = await _http.SendAsync(deleteRequest, ct);
        if (!deleteResponse.IsSuccessStatusCode)
        {
            var body = await deleteResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("Falha ao deletar categorias antigas de {VideoId}: {Body}", videoId, body);
        }

        var dtos = categorias.Select(CategoriaDto.From).ToList();
        var content = new StringContent(JsonConvert.SerializeObject(dtos, JsonSettings), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, _options.Rest("/categoria_teologica")) { Content = content };
        request.Headers.Add("Prefer", "resolution=merge-duplicates");
        var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Supabase upsert falhou em categoria_teologica: {Status} — {Body}", response.StatusCode, body);
            throw new HttpRequestException($"Supabase upsert categorias falhou: {response.StatusCode}");
        }
    }

    private async Task PostgRestUpsertAsync(string path, object dto, CancellationToken ct)
    {
        var content = new StringContent(JsonConvert.SerializeObject(dto, JsonSettings), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("Prefer", "resolution=merge-duplicates,return=representation");
        var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Supabase upsert falhou em {Path}: {Status} — {Body}", path, response.StatusCode, body);
            throw new HttpRequestException($"Supabase upsert falhou: {response.StatusCode}");
        }
    }

    // DTOs de mapeamento Supabase ↔ Domain
    // Construtores secundários convertidos em factory methods para evitar ambiguidade
    // com Newtonsoft.Json (dois construtores = incapaz de escolher qual usar na deserialização).

    private record VideoDto(string id, string? title, string? description, string? channel_id,
        string? channel_title, string? playlist_id, string? length, string? privacy_status,
        DateTime? uploaded_at, DateTime? processed_at,
        TranscricaoDto? transcricao = null, ResumoDto? resumo = null,
        List<CategoriaDto>? categoria_teologica = null,
        List<VideoChunkIndexDto>? video_chunk = null)
    {
        public static VideoDto From(Video v) => new(v.Id, v.Title, v.Description, v.ChannelId, v.ChannelTitle,
            v.PlaylistId, v.Length, v.PrivacyStatus, v.UploadedAt, v.ProcessedAt);

        public Video ToDomain() => new()
        {
            Id = id, Title = title ?? "", Description = description ?? "",
            ChannelId = channel_id ?? "", ChannelTitle = channel_title ?? "",
            PlaylistId = playlist_id ?? "", Length = length ?? "",
            PrivacyStatus = privacy_status ?? "", UploadedAt = uploaded_at, ProcessedAt = processed_at,
            Transcricao = transcricao?.ToDomain(),
            Resumo = resumo?.ToDomain(),
            Categorias = categoria_teologica?
                .Select(c => c.ToDomain())
                .Where(c => c != null)
                .Cast<CategoriaTeologica>()
                .OrderByDescending(c => c.Peso)
                .ToList() ?? [],
            Chunks = video_chunk?
                .Select(c => new VideoChunk { VideoId = id, ChunkIndex = c.chunk_index, Origem = c.origem })
                .ToList() ?? []
        };
    }

    // DTO leve — só carregamos índice e origem para saber se chunks existem por provedor, sem
    // trazer conteúdo/embedding
    private record VideoChunkIndexDto(int chunk_index, string origem);

    private record TranscricaoDto(string video_id, string conteudo, string formato, string origem, DateTime gerado_em)
    {
        public static TranscricaoDto From(Transcricao t) => new(t.VideoId, t.Conteudo, t.Formato, t.Origem, t.GeradoEm);
        public Transcricao ToDomain() => new()
        {
            VideoId       = video_id,
            Conteudo      = conteudo,
            ConteudoLimpo = TranscricaoHelper.StripTimestamps(conteudo, formato),
            Formato       = formato,
            Origem        = origem,
            GeradoEm      = gerado_em
        };
    }

    private record ResumoDto(string video_id, string descricao, string origem, DateTime gerado_em)
    {
        public static ResumoDto From(Resumo r) => new(r.VideoId, r.Descricao, r.Origem, r.GeradoEm);
        public Resumo ToDomain() => new() { VideoId = video_id, Descricao = descricao, Origem = origem, GeradoEm = gerado_em };
    }

    private record CategoriaDto(string video_id, string area, string subcategoria, decimal peso)
    {
        public static CategoriaDto From(CategoriaTeologica c) => new(c.VideoId, c.Area.ToString(), c.Subcategoria, c.Peso);
        public CategoriaTeologica? ToDomain()
        {
            if (!Enum.TryParse<Giom.VideoManager.Domain.Enums.AreaTeologica>(area, true, out var a)) return null;
            return new CategoriaTeologica { VideoId = video_id, Area = a, Subcategoria = subcategoria, Peso = peso };
        }
    }
}
