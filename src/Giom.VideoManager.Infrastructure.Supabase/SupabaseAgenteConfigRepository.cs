using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>Lê a tabela agente_config (Config First) via PostgREST. A linha do provedor vence a linha '*'.</summary>
public class SupabaseAgenteConfigRepository : IAgenteConfigRepository
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;

    public SupabaseAgenteConfigRepository(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
    }

    public async Task<string> ObterInstrucaoConcisaoAsync(string provedor, CancellationToken ct = default)
    {
        var filtro = Uri.EscapeDataString($"in.({provedor},*)");
        var json = await _http.GetStringAsync(
            _options.Rest($"/agente_config?provedor={filtro}&select=provedor,instrucao_concisao"),
            ct);
        var rows = JsonConvert.DeserializeObject<List<AgenteConfigRow>>(json) ?? [];

        var linha = rows.FirstOrDefault(r => r.provedor == provedor) ?? rows.FirstOrDefault(r => r.provedor == "*");
        return linha?.instrucao_concisao ?? string.Empty;
    }

    private record AgenteConfigRow(string provedor, string? instrucao_concisao);
}
