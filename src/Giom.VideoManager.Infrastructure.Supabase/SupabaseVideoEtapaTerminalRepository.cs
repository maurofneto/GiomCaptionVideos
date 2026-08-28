using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Net.Http.Headers;
using System.Text;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>Repositório de video_etapa_terminal via Supabase REST API (PostgREST) — ver bug #111.</summary>
public class SupabaseVideoEtapaTerminalRepository : IVideoEtapaTerminalRepository
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;
    private readonly ILogger<SupabaseVideoEtapaTerminalRepository> _logger;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        NullValueHandling = NullValueHandling.Ignore
    };

    public SupabaseVideoEtapaTerminalRepository(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory, ILogger<SupabaseVideoEtapaTerminalRepository> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = httpClientFactory.CreateClient("supabase");
    }

    public async Task<IReadOnlySet<string>> RegistrarConclusaoAsync(string videoId, string etapa, CancellationToken ct = default)
    {
        var content = new StringContent(JsonConvert.SerializeObject(new VideoEtapaTerminalDto(videoId, etapa), JsonSettings), Encoding.UTF8, "application/json");
        var upsertRequest = new HttpRequestMessage(HttpMethod.Post, _options.Rest("/video_etapa_terminal?on_conflict=video_id,etapa")) { Content = content };
        upsertRequest.Headers.Add("Prefer", "resolution=merge-duplicates");
        var upsertResponse = await _http.SendAsync(upsertRequest, ct);

        if (!upsertResponse.IsSuccessStatusCode)
        {
            var body = await upsertResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("Supabase upsert falhou em video_etapa_terminal: {Status} — {Body}", upsertResponse.StatusCode, body);
            throw new HttpRequestException($"Supabase upsert video_etapa_terminal falhou: {upsertResponse.StatusCode}");
        }

        var response = await _http.GetAsync(_options.Rest($"/video_etapa_terminal?video_id=eq.{videoId}&select=etapa"), ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var lista = JsonConvert.DeserializeObject<List<VideoEtapaTerminalDto>>(json, JsonSettings) ?? [];
        return lista.Select(d => d.etapa).ToHashSet();
    }

    private record VideoEtapaTerminalDto(string video_id, string etapa);
}
