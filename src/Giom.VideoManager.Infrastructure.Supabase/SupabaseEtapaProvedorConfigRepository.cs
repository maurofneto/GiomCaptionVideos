using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Net.Http.Headers;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>Lê a tabela etapa_provedor_config (US #106) via PostgREST — mesmo padrão HTTP de SupabaseCanalIngestaoRepository.</summary>
public class SupabaseEtapaProvedorConfigRepository : IEtapaProvedorConfigRepository
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;

    public SupabaseEtapaProvedorConfigRepository(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory)
    {
        this._options = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
    }

    public async Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(
            _options.Rest($"/etapa_provedor_config?channel_id=eq.{Uri.EscapeDataString(channelId)}&etapa=eq.{Uri.EscapeDataString(etapa)}&order=ordem.asc"),
            ct);
        var rows = JsonConvert.DeserializeObject<List<EtapaProvedorConfigRow>>(json) ?? [];

        return rows.Select(r => new EtapaProvedorConfig(
            r.channel_id, r.etapa, r.provedor, r.ordem,
            r.prompt, r.model, r.max_tokens, r.usar_observabilidade ?? false,
            Origem: "Repository")).ToList();
    }

    private record EtapaProvedorConfigRow(
        string channel_id, string etapa, string provedor, int ordem,
        string? prompt, string? model, int? max_tokens, bool? usar_observabilidade);
}
