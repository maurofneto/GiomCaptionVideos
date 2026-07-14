using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Resiliencia;

/// <summary>
/// FallbackResumoService (US #106) — único IResumoService público, resolve a cadeia do canal via
/// IEtapaProvedorConfigService (fake aqui) e chama os IResumoProvedorService Keyed na ordem que a
/// config trouxe, não numa lista fixa registrada no DI.
/// </summary>
public class FallbackResumoServiceTests
{
    private sealed class FakeEtapaProvedorConfigService(IReadOnlyList<EtapaProvedorConfig> cadeia) : IEtapaProvedorConfigService
    {
        public Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default) =>
            Task.FromResult(cadeia);
    }

    private sealed class FakeResumoProvedorService(bool falha, List<string> chamadas) : IResumoProvedorService
    {
        public Task<Resumo?> GerarResumoAsync(Video video, EtapaProvedorConfig config, CancellationToken ct = default)
        {
            chamadas.Add(config.Provedor);
            if (falha) throw new InvalidOperationException($"Falha simulada em {config.Provedor}");
            return Task.FromResult<Resumo?>(new Resumo
            {
                VideoId = video.Id, Descricao = $"resumo-{config.Provedor}", Origem = config.Provedor,
                GeradoEm = DateTime.UtcNow, Capitulos = []
            });
        }
    }

    private static EtapaProvedorConfig Config(string provedor, int ordem) =>
        new("UCteste", "resumo", provedor, ordem, $"prompt-{provedor}", Model: null, MaxTokens: null, UsarObservabilidade: false, Origem: "Repository");

    private static Video CriarVideo() => new() { Id = "v1", Title = "Teste", ChannelId = "UCteste" };

    [Fact]
    public async Task GerarResumoAsync_PrimeiroProvedorDaCadeiaSucede_NaoTentaOsDemais()
    {
        var chamadas = new List<string>();
        var cadeia = new List<EtapaProvedorConfig> { Config("Claude", 1), Config("Grok", 2) };

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IResumoProvedorService>("Claude", new FakeResumoProvedorService(falha: false, chamadas));
        services.AddKeyedSingleton<IResumoProvedorService>("Grok", new FakeResumoProvedorService(falha: false, chamadas));
        var sp = services.BuildServiceProvider();

        var fallback = new FallbackResumoService(new FakeEtapaProvedorConfigService(cadeia), sp, new SessionContext(), new FakeLogger<FallbackResumoService>());

        var resumo = await fallback.GerarResumoAsync(CriarVideo());

        Assert.Equal("resumo-Claude", resumo!.Descricao);
        Assert.Equal(["Claude"], chamadas);
    }

    [Fact]
    public async Task GerarResumoAsync_PrimeiroFalha_CaiParaOSegundoNaOrdemDaConfigDoCanal()
    {
        var chamadas = new List<string>();
        // Ordem invertida da "cadeia padrão" (Claude, Grok, OpenAI) — prova que quem manda é o
        // Ordem da config do canal, não uma lista fixa.
        var cadeia = new List<EtapaProvedorConfig> { Config("Grok", 1), Config("Claude", 2) };

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IResumoProvedorService>("Grok", new FakeResumoProvedorService(falha: true, chamadas));
        services.AddKeyedSingleton<IResumoProvedorService>("Claude", new FakeResumoProvedorService(falha: false, chamadas));
        var sp = services.BuildServiceProvider();

        var fallback = new FallbackResumoService(new FakeEtapaProvedorConfigService(cadeia), sp, new SessionContext(), new FakeLogger<FallbackResumoService>());

        var resumo = await fallback.GerarResumoAsync(CriarVideo());

        Assert.Equal("resumo-Claude", resumo!.Descricao);
        Assert.Equal(["Grok", "Claude"], chamadas);
    }
}
