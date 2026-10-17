using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests;

/// <summary>AgenteConfigService (Config First): cache curto sobre agente_config e degradação sem derrubar o bot.</summary>
public class AgenteConfigServiceTests
{
    [Fact]
    public async Task ValorFicaEmCache_ReleSoDepoisDoTtl()
    {
        var repo = new Mock<IAgenteConfigRepository>();
        repo.Setup(r => r.ObterInstrucaoConcisaoAsync("Claude", It.IsAny<CancellationToken>())).ReturnsAsync("seja breve");
        var tempo = new FakeTimeProvider();
        var service = new AgenteConfigService(repo.Object, NullLogger<AgenteConfigService>.Instance, tempo);

        Assert.Equal("seja breve", await service.ObterInstrucaoConcisaoAsync("Claude"));
        Assert.Equal("seja breve", await service.ObterInstrucaoConcisaoAsync("Claude"));
        repo.Verify(r => r.ObterInstrucaoConcisaoAsync("Claude", It.IsAny<CancellationToken>()), Times.Once);

        tempo.Advance(AgenteConfigService.Ttl + TimeSpan.FromSeconds(1));
        await service.ObterInstrucaoConcisaoAsync("Claude");
        repo.Verify(r => r.ObterInstrucaoConcisaoAsync("Claude", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task BancoIndisponivel_SemCache_DevolveVazioSemLancar()
    {
        var repo = new Mock<IAgenteConfigRepository>();
        repo.Setup(r => r.ObterInstrucaoConcisaoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("fora do ar"));
        var service = new AgenteConfigService(repo.Object, NullLogger<AgenteConfigService>.Instance);

        Assert.Equal(string.Empty, await service.ObterInstrucaoConcisaoAsync("Grok"));
    }

    [Fact]
    public async Task BancoIndisponivel_AposExpirar_DevolveUltimoValorConhecido()
    {
        var repo = new Mock<IAgenteConfigRepository>();
        repo.SetupSequence(r => r.ObterInstrucaoConcisaoAsync("Claude", It.IsAny<CancellationToken>()))
            .ReturnsAsync("seja breve")
            .ThrowsAsync(new HttpRequestException("fora do ar"));
        var tempo = new FakeTimeProvider();
        var service = new AgenteConfigService(repo.Object, NullLogger<AgenteConfigService>.Instance, tempo);

        await service.ObterInstrucaoConcisaoAsync("Claude");
        tempo.Advance(AgenteConfigService.Ttl + TimeSpan.FromSeconds(1));

        Assert.Equal("seja breve", await service.ObterInstrucaoConcisaoAsync("Claude"));
    }
}
