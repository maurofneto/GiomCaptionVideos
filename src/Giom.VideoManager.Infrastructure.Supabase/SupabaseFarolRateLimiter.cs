using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>
/// Farol de rate limit (Bug #122) via função Postgres farol_consumir (RPC) — atômica (SELECT
/// ... FOR UPDATE dentro da mesma transação da chamada RPC), então a concorrência entre tasks é
/// resolvida pelo próprio lock de linha do Postgres, sem coordenação nova do lado do worker.
/// Consumo é tudo-ou-nada entre as dimensões pedidas (requisicoes/tokens) — a função no Postgres
/// que garante isso, aqui só serializa o dicionário e faz poll até vir true.
/// </summary>
public class SupabaseFarolRateLimiter : IFarolRateLimiter
{
    private static readonly TimeSpan IntervaloPoll = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;
    private readonly ILogger<SupabaseFarolRateLimiter> _logger;

    public SupabaseFarolRateLimiter(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory, ILogger<SupabaseFarolRateLimiter> logger)
    {
        this._options = options.Value;
        _logger = logger;
        _http = httpClientFactory.CreateClient("supabase");
    }

    public async Task AguardarAsync(string provedor, IReadOnlyDictionary<string, double> consumo, CancellationToken ct = default)
    {
        var esperou = false;
        while (!await TentarConsumirAsync(provedor, consumo, ct))
        {
            if (!esperou)
            {
                _logger.LogInformation("Farol {Provedor} — sem saldo em uma ou mais dimensões ({Dimensoes}), aguardando (poll de {Intervalo}s)",
                    provedor, string.Join(", ", consumo.Keys), IntervaloPoll.TotalSeconds);
                esperou = true;
            }
            await Task.Delay(IntervaloPoll, ct);
        }
    }

    private async Task<bool> TentarConsumirAsync(string provedor, IReadOnlyDictionary<string, double> consumo, CancellationToken ct)
    {
        var payload = JsonConvert.SerializeObject(new { p_provedor = provedor, p_consumo = consumo });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(_options.Rest("/rpc/farol_consumir"), content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Farol {Provedor} — RPC farol_consumir falhou: {Status} — {Body}", provedor, response.StatusCode, body);
            throw new HttpRequestException($"Supabase RPC farol_consumir falhou: {response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<bool>(json);
    }
}
