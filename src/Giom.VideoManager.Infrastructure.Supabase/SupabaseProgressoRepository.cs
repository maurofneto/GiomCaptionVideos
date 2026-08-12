using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Net.Http.Headers;
using System.Text;

namespace Giom.VideoManager.Infrastructure.Supabase;

public class SupabaseProgressoRepository : IProgressoRepository
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;

    // NullValueHandling.Include é obrigatório aqui: last_page_token PRECISA poder ser gravado
    // como null quando a playlist termina (ChannelProcessor reseta o cursor pra próxima run
    // recomeçar do topo com filtro de data). Com Ignore, o campo null some do JSON e o upsert
    // (Prefer: resolution=merge-duplicates) simplesmente não toca a coluna existente — o reset
    // nunca acontecia de verdade, prendendo a ingestão num cursor morto pra sempre (Bug #136:
    // canais travados em 2026-07-18, nunca mais viram vídeo novo).
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        NullValueHandling = NullValueHandling.Include
    };

    public SupabaseProgressoRepository(IOptions<SupabaseOptions> options)
    {
        _options = options.Value;
        _http = new HttpClient { BaseAddress = new Uri(_options.Url) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
        _http.DefaultRequestHeaders.Add("apikey", _options.SecretKey);
    }

    public async Task<string?> GetLastPageTokenAsync(string channelKey, string playlistId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"/rest/v1/processamento_progresso?channel_key=eq.{channelKey}&playlist_id=eq.{playlistId}&select=last_page_token", ct);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        var list = JsonConvert.DeserializeObject<List<Dictionary<string, string?>>>(json);
        return list?.FirstOrDefault()?.GetValueOrDefault("last_page_token");
    }

    public async Task<DateTime?> GetLastProcessedDateAsync(string channelKey, string playlistId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"/rest/v1/processamento_progresso?channel_key=eq.{channelKey}&playlist_id=eq.{playlistId}&select=last_processed_date", ct);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        var list = JsonConvert.DeserializeObject<List<Dictionary<string, string?>>>(json);
        var value = list?.FirstOrDefault()?.GetValueOrDefault("last_processed_date");
        return value != null ? DateTime.Parse(value) : null;
    }

    public async Task SaveProgressoAsync(string channelKey, string playlistId, string? pageToken, DateTime? maxProcessedDate, CancellationToken ct = default)
    {
        var dto = new
        {
            channel_key = channelKey,
            playlist_id = playlistId,
            last_page_token = pageToken,
            last_processed_date = maxProcessedDate,
            last_execution_date = DateTime.UtcNow
        };

        var content = new StringContent(JsonConvert.SerializeObject(dto, JsonSettings), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/rest/v1/processamento_progresso") { Content = content };
        request.Headers.Add("Prefer", "resolution=merge-duplicates");
        await _http.SendAsync(request, ct);
    }
}
