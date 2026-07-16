using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Testes.Comuns;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Resiliencia;

/// <summary>
/// PipelineEmbeddingService (Feature #115/US #116) — variante de IEmbeddingService pra
/// indexação: ao contrário de FallbackEmbeddingService (busca, para no primeiro sucesso), a
/// indexação roda TODOS os provedores configurados; uma falha de um não impede os outros.
/// BuscarAsync mantém a mesma semântica de fallback (não é o foco desta troca).
/// </summary>
public class PipelineEmbeddingServiceTests
{
    private sealed class FakeEmbeddingProvider(string nome, bool falha, List<string> chamadas) : IEmbeddingService
    {
        public Task IndexarAsync(Video video, CancellationToken ct = default)
        {
            chamadas.Add(nome);
            if (falha) throw new HttpRequestException($"Falha simulada em {nome}");
            return Task.CompletedTask;
        }

        public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
        {
            chamadas.Add(nome);
            if (falha) throw new HttpRequestException($"Falha simulada em {nome}");
            return Task.FromResult(new BuscaSemanticaResultado([], $"contexto-{nome}"));
        }
    }

    private static Video CriarVideo() => new() { Id = "v1", Title = "Teste", ChannelId = "UCteste" };

    [Fact]
    public async Task IndexarAsync_TresProvedoresConfigurados_ChamaTodosOsTresMesmoComUmaFalha()
    {
        var chamadas = new List<string>();
        var cadeia = new List<(string Nome, IEmbeddingService Servico)>
        {
            ("OpenAI", new FakeEmbeddingProvider("OpenAI", falha: true, chamadas)),
            ("VoyageAI", new FakeEmbeddingProvider("VoyageAI", falha: false, chamadas)),
            ("Cohere", new FakeEmbeddingProvider("Cohere", falha: false, chamadas)),
        };

        var pipeline = new PipelineEmbeddingService(cadeia, new FakeLogger<PipelineEmbeddingService>());

        await pipeline.IndexarAsync(CriarVideo());

        // Nenhuma exceção propaga, e os 3 são chamados mesmo com a falha do primeiro — ao
        // contrário do fallback, que pararia aí.
        Assert.Equal(["OpenAI", "VoyageAI", "Cohere"], chamadas);
    }

    [Fact]
    public async Task BuscarAsync_MantemSemanticaDeFallback_ParaNoPrimeiroQueFunciona()
    {
        var chamadas = new List<string>();
        var cadeia = new List<(string Nome, IEmbeddingService Servico)>
        {
            ("OpenAI", new FakeEmbeddingProvider("OpenAI", falha: true, chamadas)),
            ("VoyageAI", new FakeEmbeddingProvider("VoyageAI", falha: false, chamadas)),
            ("Cohere", new FakeEmbeddingProvider("Cohere", falha: false, chamadas)),
        };

        var pipeline = new PipelineEmbeddingService(cadeia, new FakeLogger<PipelineEmbeddingService>());

        var resultado = await pipeline.BuscarAsync("pergunta qualquer");

        Assert.Equal("contexto-VoyageAI", resultado.ContextoFormatado);
        Assert.Equal(["OpenAI", "VoyageAI"], chamadas); // Cohere nem chega a ser chamado
    }
}
