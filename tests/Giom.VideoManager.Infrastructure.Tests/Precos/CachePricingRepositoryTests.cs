using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Precos;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Precos;

/// <summary>Decorator de cache em memória (Task #130) — hit não bate de novo no inner; (provedor, modelo) diferentes não colidem; case-insensitive.</summary>
public class CachePricingRepositoryTests
{
    private sealed class ContadorPricingRepository : IPricingRepository
    {
        public int Chamadas { get; private set; }

        public Task<Preco?> ObterAsync(string provedor, string modelo, CancellationToken ct = default)
        {
            Chamadas++;
            return Task.FromResult<Preco?>(new Preco(provedor, modelo, 1.00m, 2.00m));
        }
    }

    [Fact]
    public async Task ObterAsync_MesmoProvedorEModelo_SoBateNoInnerUmaVez()
    {
        var inner = new ContadorPricingRepository();
        var cache = new CachePricingRepository(inner);

        await cache.ObterAsync("Claude", "claude-sonnet-5");
        await cache.ObterAsync("Claude", "claude-sonnet-5");
        await cache.ObterAsync("Claude", "claude-sonnet-5");

        Assert.Equal(1, inner.Chamadas);
    }

    [Fact]
    public async Task ObterAsync_ProvedorOuModeloDiferente_BateNoInnerDeNovo()
    {
        var inner = new ContadorPricingRepository();
        var cache = new CachePricingRepository(inner);

        await cache.ObterAsync("Claude", "claude-sonnet-5");
        await cache.ObterAsync("OpenAI", "claude-sonnet-5");
        await cache.ObterAsync("Claude", "claude-haiku-4-5");

        Assert.Equal(3, inner.Chamadas);
    }

    [Fact]
    public async Task ObterAsync_ProvedorEModeloCaseInsensitive_ContaComoMesmaChaveDeCache()
    {
        var inner = new ContadorPricingRepository();
        var cache = new CachePricingRepository(inner);

        await cache.ObterAsync("Claude", "claude-sonnet-5");
        await cache.ObterAsync("CLAUDE", "CLAUDE-SONNET-5");

        Assert.Equal(1, inner.Chamadas);
    }
}
