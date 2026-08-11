using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Net.Http.Headers;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>Lê a tabela provedor_preco (Task #130) via PostgREST — mesmo padrão HTTP de SupabaseEtapaProvedorConfigRepository.</summary>
public class SupabasePricingRepository : IPricingRepository
{
    private readonly HttpClient _http;

    public SupabasePricingRepository(IOptions<SupabaseOptions> options)
    {
        var opt = options.Value;
        _http = new HttpClient { BaseAddress = new Uri(opt.Url) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opt.SecretKey);
        _http.DefaultRequestHeaders.Add("apikey", opt.SecretKey);
    }

    public async Task<Preco?> ObterAsync(string provedor, string modelo, CancellationToken ct = default)
    {
        // ilike (não eq) preserva a comparação case-insensitive que o PricingCalculator estático
        // tinha via IEqualityComparer dedicado — provedor/modelo não têm caracteres coringa (%/_),
        // então ilike funciona como igualdade exata ignorando maiúsculas/minúsculas.
        var json = await _http.GetStringAsync(
            $"/rest/v1/provedor_preco?provedor=ilike.{Uri.EscapeDataString(provedor)}&modelo=ilike.{Uri.EscapeDataString(modelo)}&limit=1",
            ct);
        var rows = JsonConvert.DeserializeObject<List<PrecoRow>>(json) ?? [];
        var row = rows.FirstOrDefault();

        return row is null ? null : new Preco(row.provedor, row.modelo, row.entrada_por_milhao, row.saida_por_milhao);
    }

    private record PrecoRow(string provedor, string modelo, decimal entrada_por_milhao, decimal saida_por_milhao);
}
