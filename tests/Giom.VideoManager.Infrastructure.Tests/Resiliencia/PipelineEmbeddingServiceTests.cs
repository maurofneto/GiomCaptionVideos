using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Resiliencia;

/// <summary>
/// PipelineEmbeddingService (US #129, mesmo mecanismo de FallbackResumoService/US #106) — único
/// implementador de IVideoIndexingService: resolve a cadeia do canal via
/// IEtapaProvedorConfigService (fake aqui) e roda TODOS os provedores dela na indexação, ao
/// contrário de FallbackEmbeddingService (busca, ver FallbackEmbeddingServiceTests) que para no
/// primeiro sucesso — uma falha de um provedor não impede os outros de indexarem.
/// </summary>
public class PipelineEmbeddingServiceTests
{
    private sealed class FakeEtapaProvedorConfigService(IReadOnlyList<EtapaProvedorConfig> cadeia) : IEtapaProvedorConfigService
    {
        public Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default) =>
            Task.FromResult(cadeia);
    }

    private sealed class FakeEmbeddingProvider(string nome, bool falha, List<string> chamadas) : IEmbeddingService
    {
        public Task IndexarAsync(Video video, CancellationToken ct = default)
        {
            chamadas.Add(nome);
            if (falha) throw new HttpRequestException($"Falha simulada em {nome}");
            return Task.CompletedTask;
        }

        public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
            => throw new NotSupportedException("Fake não implementa busca — este teste só cobre indexação.");
    }

    private static EtapaProvedorConfig Config(string provedor, int ordem) =>
        new("UCteste", "embedding", provedor, ordem, Prompt: null, Model: null, MaxTokens: null, UsarObservabilidade: false, Origem: "Repository");

    private static Video CriarVideo() => new() { Id = "v1", Title = "Teste", ChannelId = "UCteste" };

    [Fact]
    public async Task IndexarAsync_TresProvedoresNaCadeiaDoCanal_ChamaTodosOsTresMesmoComUmaFalha()
    {
        var chamadas = new List<string>();
        var cadeia = new List<EtapaProvedorConfig> { Config("OpenAI", 1), Config("VoyageAI", 2), Config("Cohere", 3) };

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IEmbeddingService>("OpenAI", new FakeEmbeddingProvider("OpenAI", falha: true, chamadas));
        services.AddKeyedSingleton<IEmbeddingService>("VoyageAI", new FakeEmbeddingProvider("VoyageAI", falha: false, chamadas));
        services.AddKeyedSingleton<IEmbeddingService>("Cohere", new FakeEmbeddingProvider("Cohere", falha: false, chamadas));
        var sp = services.BuildServiceProvider();

        var pipeline = new PipelineEmbeddingService(new FakeEtapaProvedorConfigService(cadeia), sp, new FakeLogger<PipelineEmbeddingService>());

        await pipeline.IndexarAsync(CriarVideo());

        // Nenhuma exceção propaga, e os 3 são chamados mesmo com a falha do primeiro — ao
        // contrário do fallback, que pararia aí.
        Assert.Equal(["OpenAI", "VoyageAI", "Cohere"], chamadas);
    }

    [Fact]
    public async Task IndexarAsync_UmProvedorNaCadeia_ChamaSoEsse()
    {
        var chamadas = new List<string>();
        var cadeia = new List<EtapaProvedorConfig> { Config("OpenAI", 1) };

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IEmbeddingService>("OpenAI", new FakeEmbeddingProvider("OpenAI", falha: false, chamadas));
        var sp = services.BuildServiceProvider();

        var pipeline = new PipelineEmbeddingService(new FakeEtapaProvedorConfigService(cadeia), sp, new FakeLogger<PipelineEmbeddingService>());

        await pipeline.IndexarAsync(CriarVideo());

        Assert.Equal(["OpenAI"], chamadas);
    }

    [Fact]
    public async Task IndexarAsync_CadeiaVazia_LancaEmVezDeReportarSucessoSilencioso()
    {
        // Bug #134 — mesma classe de falha: canal sem config pra "embedding" não pode virar um
        // no-op que o orquestrador trata como concluído com sucesso.
        var sp = new ServiceCollection().BuildServiceProvider();
        var pipeline = new PipelineEmbeddingService(new FakeEtapaProvedorConfigService([]), sp, new FakeLogger<PipelineEmbeddingService>());

        await Assert.ThrowsAsync<NenhumResultadoEncontradoException>(() => pipeline.IndexarAsync(CriarVideo()));
    }
}
