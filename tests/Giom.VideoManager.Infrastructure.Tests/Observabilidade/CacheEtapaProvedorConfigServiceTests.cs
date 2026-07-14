using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Observabilidade;

/// <summary>Decorator de cache em memória (US #106) — hit não bate de novo no inner; (canal, etapa) diferentes não colidem.</summary>
public class CacheEtapaProvedorConfigServiceTests
{
    private sealed class ContadorEtapaProvedorConfigService : IEtapaProvedorConfigService
    {
        public int Chamadas { get; private set; }

        public Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default)
        {
            Chamadas++;
            IReadOnlyList<EtapaProvedorConfig> cadeia = [new(channelId, etapa, "Claude", 1, "prompt", null, null, false, "Repository")];
            return Task.FromResult(cadeia);
        }
    }

    [Fact]
    public async Task ObterCadeiaAsync_MesmoCanalEEtapa_SoBateNoInnerUmaVez()
    {
        var inner = new ContadorEtapaProvedorConfigService();
        var cache = new CacheEtapaProvedorConfigService(inner);

        await cache.ObterCadeiaAsync("UCteste", "resumo");
        await cache.ObterCadeiaAsync("UCteste", "resumo");
        await cache.ObterCadeiaAsync("UCteste", "resumo");

        Assert.Equal(1, inner.Chamadas);
    }

    [Fact]
    public async Task ObterCadeiaAsync_CanalOuEtapaDiferente_BateNoInnerDeNovo()
    {
        var inner = new ContadorEtapaProvedorConfigService();
        var cache = new CacheEtapaProvedorConfigService(inner);

        await cache.ObterCadeiaAsync("UCteste", "resumo");
        await cache.ObterCadeiaAsync("UCoutro", "resumo");
        await cache.ObterCadeiaAsync("UCteste", "categorizacao");

        Assert.Equal(3, inner.Chamadas);
    }
}
