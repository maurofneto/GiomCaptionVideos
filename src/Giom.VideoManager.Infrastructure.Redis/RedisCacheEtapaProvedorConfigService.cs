using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace Giom.VideoManager.Infrastructure.Redis;

public class RedisCacheOptions
{
    public string ConnectionString { get; init; } = string.Empty;
    public int TtlSegundos { get; init; } = 300;
}

/// <summary>
/// Decorator de cache Redis (US #106) pra IEtapaProvedorConfigService — alternativa ao cache em
/// memória (CacheEtapaProvedorConfigService) quando N instâncias do worker precisam compartilhar
/// o cache (cenário de Service de vida longa com auto scaling por fila, ainda não implementado;
/// hoje Resumo/Categorizacao.Worker são processos curtos, memória já basta). TTL configurável —
/// diferente do cache em memória, aqui expira de verdade porque o processo não é descartado a
/// cada run.
/// </summary>
public sealed class RedisCacheEtapaProvedorConfigService(
    IEtapaProvedorConfigService inner,
    IConnectionMultiplexer conexao,
    Microsoft.Extensions.Options.IOptions<RedisCacheOptions> options) : IEtapaProvedorConfigService
{
    private readonly RedisCacheOptions _options = options.Value;

    public async Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default)
    {
        var db = conexao.GetDatabase();
        var chave = $"etapa-provedor-config:{channelId}:{etapa}";

        var cacheado = await db.StringGetAsync(chave);
        if (cacheado.HasValue)
            return JsonConvert.DeserializeObject<List<EtapaProvedorConfig>>(cacheado!)!;

        var cadeia = await inner.ObterCadeiaAsync(channelId, etapa, ct);
        await db.StringSetAsync(chave, JsonConvert.SerializeObject(cadeia), TimeSpan.FromSeconds(_options.TtlSegundos));
        return cadeia;
    }
}
