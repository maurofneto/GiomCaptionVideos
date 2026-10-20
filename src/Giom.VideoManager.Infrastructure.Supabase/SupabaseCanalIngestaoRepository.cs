using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Net.Http.Headers;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>Lê a tabela canal_ingestao (US #105) via PostgREST — mesmo padrão HTTP de SupabaseProgressoRepository/SupabaseSecretsProvider.</summary>
public class SupabaseCanalIngestaoRepository : ICanalIngestaoRepository
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;

    public SupabaseCanalIngestaoRepository(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory)
    {
        this._options = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
    }

    public async Task<IReadOnlyList<CanalIngestaoConfig>> GetCanaisAtivosAsync(CancellationToken ct = default)
    {
        var response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, _options.Rest("/canal_ingestao?ativo=eq.true")), ct);
        var json = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();

        var rows = JsonConvert.DeserializeObject<List<CanalIngestaoRow>>(json) ?? [];

        return rows.Select(r => new CanalIngestaoConfig(
            r.channel_id, r.playlist_id, r.nome, r.mine, r.ativo,
            ParseRelevanciaTipo(r.relevancia_tipo), r.relevancia_valores,
            r.max_videos_per_run, r.delay_between_videos_ms, r.delay_between_pages_ms,
            r.secrets_key, r.privacy_status_filtro)).ToList();
    }

    private static RelevanciaTipo ParseRelevanciaTipo(string valor) => valor switch
    {
        "live_unlisted" => RelevanciaTipo.LiveUnlisted,
        "titulo_contem" => RelevanciaTipo.TituloContem,
        _ => throw new InvalidOperationException($"relevancia_tipo desconhecido: {valor}")
    };

    private record CanalIngestaoRow(
        string channel_id, string? playlist_id, string nome, bool mine, bool ativo,
        string relevancia_tipo, List<string> relevancia_valores,
        int max_videos_per_run, int delay_between_videos_ms, int delay_between_pages_ms,
        string secrets_key, string? privacy_status_filtro = null);
}
