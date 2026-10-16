using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>Lê a tabela provedor_preco (Task #130) via PostgREST — mesmo padrão HTTP de SupabaseEtapaProvedorConfigRepository.</summary>
public class SupabasePricingRepository : IPricingRepository
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;

    public SupabasePricingRepository(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
    }

    public async Task<Preco?> ObterAsync(string provedor, string modelo, CancellationToken ct = default)
    {
        // ilike (não eq) preserva a comparação case-insensitive que o PricingCalculator estático
        // tinha via IEqualityComparer dedicado — provedor/modelo não têm caracteres coringa (%/_),
        // então ilike funciona como igualdade exata ignorando maiúsculas/minúsculas.
        var json = await _http.GetStringAsync(
            _options.Rest($"/provedor_preco?provedor=ilike.{Uri.EscapeDataString(provedor)}&modelo=ilike.{Uri.EscapeDataString(modelo)}&limit=1"),
            ct);
        var rows = JsonConvert.DeserializeObject<List<PrecoRow>>(json) ?? [];
        var row = rows.FirstOrDefault();

        return row is null ? null : new Preco(row.provedor, row.modelo, row.entrada_por_milhao, row.saida_por_milhao);
    }

    private record PrecoRow(string provedor, string modelo, decimal entrada_por_milhao, decimal saida_por_milhao);
}
