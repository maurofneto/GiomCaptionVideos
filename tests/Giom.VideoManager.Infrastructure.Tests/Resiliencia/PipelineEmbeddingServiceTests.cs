using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Testes.Comuns;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Resiliencia;

/// <summary>
/// PipelineEmbeddingService (Feature #115/US #116) — único implementador de
/// IVideoIndexingService: roda TODOS os provedores configurados na indexação, ao contrário de
/// FallbackEmbeddingService (busca, ver FallbackEmbeddingServiceTests) que para no primeiro
/// sucesso — uma falha de um provedor não impede os outros de indexarem.
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
            => throw new NotSupportedException("Fake não implementa busca — este teste só cobre indexação.");
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
}
