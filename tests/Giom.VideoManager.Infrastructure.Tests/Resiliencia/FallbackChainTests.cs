using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Testes.Comuns;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Resiliencia;

public class FallbackChainTests
{
    private static (List<(string Nome, Func<CancellationToken, Task<string>> Executar)> Elos, List<string> Chamadas) MontarElos(params (string Nome, bool Falha)[] especificacao)
    {
        var chamadas = new List<string>();
        var elos = especificacao.Select(e => (e.Nome, (Func<CancellationToken, Task<string>>)(_ =>
        {
            chamadas.Add(e.Nome);
            if (e.Falha) throw new InvalidOperationException($"Falha simulada em {e.Nome}");
            return Task.FromResult(e.Nome);
        }))).ToList();

        return (elos, chamadas);
    }

    [Fact]
    public async Task ExecutarAsync_PrimeiroEloSucede_NaoTentaOsDemaisNemLogaFallback()
    {
        var (elos, chamadas) = MontarElos(("A", false), ("B", false), ("C", false));
        var logger = new FakeLogger<object>();

        var resultado = await FallbackChain.Montar(elos).ExecutarAsync("etapa", logger, CancellationToken.None);

        Assert.Equal("A", resultado);
        Assert.Equal(["A"], chamadas);
        Assert.Empty(logger.Entradas);
    }

    [Fact]
    public async Task ExecutarAsync_PrimeiroFalhaSegundoSucede_CaiParaOSegundoELogaErro()
    {
        var (elos, chamadas) = MontarElos(("A", true), ("B", false), ("C", false));
        var logger = new FakeLogger<object>();

        var resultado = await FallbackChain.Montar(elos).ExecutarAsync("etapa", logger, CancellationToken.None);

        Assert.Equal("B", resultado);
        Assert.Equal(["A", "B"], chamadas);
        Assert.Single(logger.Entradas);
        Assert.Contains("Fallback acionado", logger.Entradas[0].Mensagem);
        Assert.Contains("A", logger.Entradas[0].Mensagem);
    }

    [Fact]
    public async Task ExecutarAsync_TodosFalham_PropagaAExcecaoDoUltimoEloSemCapturar()
    {
        var (elos, chamadas) = MontarElos(("A", true), ("B", true), ("C", true));
        var logger = new FakeLogger<object>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => FallbackChain.Montar(elos).ExecutarAsync("etapa", logger, CancellationToken.None));

        Assert.Contains("C", ex.Message);
        Assert.Equal(["A", "B", "C"], chamadas);
        // Loga a falha de A e de B (repassando pro próximo) — mas não a de C (último elo, propaga direto)
        Assert.Equal(2, logger.Entradas.Count);
    }

    [Fact]
    public async Task Montar_CadeiaDeUmSoElo_FalhaPropagaDireto()
    {
        var (elos, _) = MontarElos(("Único", true));
        var logger = new FakeLogger<object>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FallbackChain.Montar(elos).ExecutarAsync("etapa", logger, CancellationToken.None));

        Assert.Empty(logger.Entradas);
    }
}
