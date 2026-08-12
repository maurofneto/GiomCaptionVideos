using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Resiliencia;

/// <summary>
/// FallbackEmbeddingService (US #129, mesmo mecanismo de FallbackResumoService/US #106) — único
/// implementador de IBuscaSemanticaService: resolve a cadeia do canal via
/// IEtapaProvedorConfigService (fake aqui), tenta os provedores na ordem que a config trouxe,
/// para no primeiro que funcionar. Ao contrário de PipelineEmbeddingService (indexação, roda
/// todos — ver PipelineEmbeddingServiceTests).
/// </summary>
public class FallbackEmbeddingServiceTests
{
    private sealed class FakeEtapaProvedorConfigService(IReadOnlyList<EtapaProvedorConfig> cadeia) : IEtapaProvedorConfigService
    {
        public Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default) =>
            Task.FromResult(cadeia);
    }

    private sealed class FakeEmbeddingProvider(bool falha, List<string> chamadas) : IEmbeddingService
    {
        public Task IndexarAsync(Video video, CancellationToken ct = default)
            => throw new NotSupportedException("Fake não implementa indexação — este teste só cobre busca.");

        public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
        {
            chamadas.Add(canalId!);
            if (falha) throw new HttpRequestException("Falha simulada");
            return Task.FromResult(new BuscaSemanticaResultado([], $"contexto-{canalId}"));
        }
    }

    private static EtapaProvedorConfig Config(string provedor, int ordem) =>
        new("UCteste", "embedding", provedor, ordem, Prompt: null, Model: null, MaxTokens: null, UsarObservabilidade: false, Origem: "Repository");

    [Fact]
    public async Task BuscarAsync_PrimeiroProvedorDaCadeiaFunciona_NaoTentaOsDemais()
    {
        var chamadas = new List<string>();
        var cadeia = new List<EtapaProvedorConfig> { Config("OpenAI", 1), Config("VoyageAI", 2) };

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IEmbeddingService>("OpenAI", new FakeEmbeddingProvider(falha: false, chamadas));
        services.AddKeyedSingleton<IEmbeddingService>("VoyageAI", new FakeEmbeddingProvider(falha: false, chamadas));
        var sp = services.BuildServiceProvider();

        var fallback = new FallbackEmbeddingService(new FakeEtapaProvedorConfigService(cadeia), sp, new FakeLogger<FallbackEmbeddingService>());

        var resultado = await fallback.BuscarAsync("pergunta qualquer", canalId: "UCteste");

        Assert.Equal("contexto-UCteste", resultado.ContextoFormatado);
        Assert.Single(chamadas);
    }

    [Fact]
    public async Task BuscarAsync_PrimeiroFalha_CaiParaOSegundoNaOrdemDaConfigDoCanal()
    {
        var chamadas = new List<string>();
        // Ordem invertida da "cadeia padrão" — prova que quem manda é o Ordem da config do
        // canal, não uma lista fixa registrada no DI.
        var cadeia = new List<EtapaProvedorConfig> { Config("VoyageAI", 1), Config("OpenAI", 2), Config("Cohere", 3) };

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IEmbeddingService>("VoyageAI", new FakeEmbeddingProvider(falha: true, chamadas));
        services.AddKeyedSingleton<IEmbeddingService>("OpenAI", new FakeEmbeddingProvider(falha: false, chamadas));
        services.AddKeyedSingleton<IEmbeddingService>("Cohere", new FakeEmbeddingProvider(falha: false, chamadas));
        var sp = services.BuildServiceProvider();

        var fallback = new FallbackEmbeddingService(new FakeEtapaProvedorConfigService(cadeia), sp, new FakeLogger<FallbackEmbeddingService>());

        var resultado = await fallback.BuscarAsync("pergunta qualquer", canalId: "UCteste");

        Assert.Equal("contexto-UCteste", resultado.ContextoFormatado);
        Assert.Equal(2, chamadas.Count); // VoyageAI (falhou) + OpenAI — Cohere nem chega a ser chamado
    }

    [Fact]
    public async Task BuscarAsync_SemCanalId_LancaExplicando()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var fallback = new FallbackEmbeddingService(new FakeEtapaProvedorConfigService([]), sp, new FakeLogger<FallbackEmbeddingService>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => fallback.BuscarAsync("pergunta qualquer", canalId: null));
    }
}
