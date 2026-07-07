using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Net.Http.Headers;
using System.Text;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>
/// Auditoria/observabilidade genérica de operações de IA persistida no Supabase (tabela
/// interacao_log). Fire-and-forget por natureza: uma falha ao gravar telemetria nunca deve
/// derrubar a operação de negócio (resumo/categorização/bot) que está sendo instrumentada —
/// por isso RegistrarAsync nunca lança, apenas loga um warning e segue.
/// </summary>
public class SupabaseTelemetriaService : ITelemetriaSink
{
    private readonly HttpClient _http;
    private readonly ILogger<SupabaseTelemetriaService> _logger;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        NullValueHandling = NullValueHandling.Ignore
    };

    public SupabaseTelemetriaService(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory, ILogger<SupabaseTelemetriaService> logger)
    {
        var sb = options.Value;
        _logger = logger;
        _http = httpClientFactory.CreateClient("supabase");
        _http.BaseAddress = new Uri(sb.Url);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sb.SecretKey);
        _http.DefaultRequestHeaders.Add("apikey", sb.SecretKey);
    }

    public async Task RegistrarAsync(RegistroTelemetria registro, CancellationToken ct = default)
    {
        try
        {
            var dto = new
            {
                tipo = registro.Tipo,
                entidade_id = registro.EntidadeId,
                provedor = registro.Provedor,
                modelo = registro.Modelo,
                tempo_ms = registro.TempoMs,
                tokens_entrada = registro.TokensEntrada,
                tokens_saida = registro.TokensSaida,
                custo_estimado_usd = registro.CustoEstimadoUsd,
                sucesso = registro.Sucesso,
                detalhes = registro.Detalhes
            };

            var content = new StringContent(JsonConvert.SerializeObject(dto, JsonSettings), Encoding.UTF8, "application/json");
            await _http.PostAsync("/rest/v1/interacao_log", content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao gravar telemetria ({Tipo}/{EntidadeId}) — ignorada, não deve afetar a operação principal", registro.Tipo, registro.EntidadeId);
        }
    }
}
