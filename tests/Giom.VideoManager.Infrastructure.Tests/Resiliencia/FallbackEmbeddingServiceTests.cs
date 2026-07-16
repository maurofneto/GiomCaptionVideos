using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Testes.Comuns;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Resiliencia;

/// <summary>
/// FallbackEmbeddingService (Feature #115) — único implementador de IBuscaSemanticaService:
/// tenta os provedores em ordem, para no primeiro que funcionar. Ao contrário de
/// PipelineEmbeddingService (indexação, roda todos — ver PipelineEmbeddingServiceTests).
/// </summary>
public class FallbackEmbeddingServiceTests
{
    private sealed class FakeEmbeddingProvider(string nome, bool falha, List<string> chamadas) : IEmbeddingService
    {
        public Task IndexarAsync(Video video, CancellationToken ct = default)
            => throw new NotSupportedException("Fake não implementa indexação — este teste só cobre busca.");

        public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
        {
            chamadas.Add(nome);
            if (falha) throw new HttpRequestException($"Falha simulada em {nome}");
            return Task.FromResult(new BuscaSemanticaResultado([], $"contexto-{nome}"));
        }
    }

    [Fact]
    public async Task BuscarAsync_PrimeiroProvedorFunciona_NaoTentaOsDemais()
    {
        var chamadas = new List<string>();
        var cadeia = new List<(string Nome, IEmbeddingService Servico)>
        {
            ("OpenAI", new FakeEmbeddingProvider("OpenAI", falha: false, chamadas)),
            ("VoyageAI", new FakeEmbeddingProvider("VoyageAI", falha: false, chamadas)),
        };

        var fallback = new FallbackEmbeddingService(cadeia, new FakeLogger<FallbackEmbeddingService>());

        var resultado = await fallback.BuscarAsync("pergunta qualquer");

        Assert.Equal("contexto-OpenAI", resultado.ContextoFormatado);
        Assert.Equal(["OpenAI"], chamadas);
    }

    [Fact]
    public async Task BuscarAsync_PrimeiroFalha_CaiParaOSegundoEParaAli()
    {
        var chamadas = new List<string>();
        var cadeia = new List<(string Nome, IEmbeddingService Servico)>
        {
            ("OpenAI", new FakeEmbeddingProvider("OpenAI", falha: true, chamadas)),
            ("VoyageAI", new FakeEmbeddingProvider("VoyageAI", falha: false, chamadas)),
            ("Cohere", new FakeEmbeddingProvider("Cohere", falha: false, chamadas)),
        };

        var fallback = new FallbackEmbeddingService(cadeia, new FakeLogger<FallbackEmbeddingService>());

        var resultado = await fallback.BuscarAsync("pergunta qualquer");

        Assert.Equal("contexto-VoyageAI", resultado.ContextoFormatado);
        Assert.Equal(["OpenAI", "VoyageAI"], chamadas); // Cohere nem chega a ser chamado
    }
}
