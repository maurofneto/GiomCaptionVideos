using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>Lê a tabela etapa_topologia (Config First) via PostgREST — mesmo padrão HTTP de SupabaseEtapaProvedorConfigRepository.</summary>
public class SupabaseTopologiaRepository : ITopologiaRepository
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;

    public SupabaseTopologiaRepository(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
    }

    public async Task<TopologiaCanal?> ObterAsync(string channelId, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(
            _options.Rest($"/etapa_topologia?channel_id=eq.{Uri.EscapeDataString(channelId)}&select=etapa,proximas"),
            ct);
        var rows = JsonConvert.DeserializeObject<List<EtapaTopologiaRow>>(json) ?? [];

        if (rows.Count == 0)
            return null;

        return new TopologiaCanal(rows.ToDictionary(r => r.etapa, r => r.proximas ?? []));
    }

    private record EtapaTopologiaRow(string etapa, string[]? proximas);
}
