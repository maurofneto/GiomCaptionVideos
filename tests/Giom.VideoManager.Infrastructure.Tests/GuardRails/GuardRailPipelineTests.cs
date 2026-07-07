using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.GuardRails;
using Giom.VideoManager.Testes.Comuns;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.GuardRails;

/// <summary>Guardrail de teste — nunca é usado em produção, só existe pra exercitar o GuardRailPipeline isoladamente.</summary>
file sealed class GuardRailDeTeste(string nome, GuardRailFase fase, GuardRailResultado resultado) : IGuardRail
{
    public List<GuardRailContexto> Chamadas { get; } = [];

    public string Nome => nome;
    public GuardRailFase Fase => fase;

    public Task<GuardRailResultado> AvaliarAsync(GuardRailContexto contexto, CancellationToken ct = default)
    {
        Chamadas.Add(contexto);
        return Task.FromResult(resultado);
    }
}

public class GuardRailPipelineTests
{
    [Fact]
    public async Task AvaliarEntradaAsync_SemGuardRailsRegistrados_SempreLibera()
    {
        var pipeline = new GuardRailPipeline([], new FakeLogger<GuardRailPipeline>());
        var resultado = await pipeline.AvaliarEntradaAsync(new GuardRailContexto("chat1", "pergunta"));

        Assert.True(resultado.Permitido);
    }

    [Fact]
    public async Task AvaliarEntradaAsync_RodaSoOsGuardRailsDaFaseEntrada_IgnorandoOsDeSaida()
    {
        var entrada = new GuardRailDeTeste("entrada", GuardRailFase.Entrada, GuardRailResultado.Ok);
        var saida = new GuardRailDeTeste("saida", GuardRailFase.Saida, GuardRailResultado.Ok);
        var pipeline = new GuardRailPipeline([entrada, saida], new FakeLogger<GuardRailPipeline>());

        await pipeline.AvaliarEntradaAsync(new GuardRailContexto("chat1", "pergunta"));

        Assert.Single(entrada.Chamadas);
        Assert.Empty(saida.Chamadas);
    }

    [Fact]
    public async Task AvaliarEntradaAsync_QuandoUmGuardRailBloqueia_ParaImediatamenteENaoRodaOsProximos()
    {
        var bloqueia = new GuardRailDeTeste("primeiro", GuardRailFase.Entrada, GuardRailResultado.Bloquear("bloqueado", "motivo"));
        var segundo = new GuardRailDeTeste("segundo", GuardRailFase.Entrada, GuardRailResultado.Ok);
        var pipeline = new GuardRailPipeline([bloqueia, segundo], new FakeLogger<GuardRailPipeline>());

        var resultado = await pipeline.AvaliarEntradaAsync(new GuardRailContexto("chat1", "pergunta"));

        Assert.False(resultado.Permitido);
        Assert.Equal("bloqueado", resultado.RespostaSubstituta);
        Assert.Empty(segundo.Chamadas); // nunca chegou a rodar — o primeiro já bloqueou
    }

    [Fact]
    public async Task AvaliarSaidaAsync_RodaSoOsGuardRailsDaFaseSaida()
    {
        var entrada = new GuardRailDeTeste("entrada", GuardRailFase.Entrada, GuardRailResultado.Ok);
        var saida = new GuardRailDeTeste("saida", GuardRailFase.Saida, GuardRailResultado.Bloquear("bloqueado", "motivo"));
        var pipeline = new GuardRailPipeline([entrada, saida], new FakeLogger<GuardRailPipeline>());

        var resultado = await pipeline.AvaliarSaidaAsync(new GuardRailContexto("chat1", "pergunta", "resposta"));

        Assert.False(resultado.Permitido);
        Assert.Empty(entrada.Chamadas);
        Assert.Single(saida.Chamadas);
    }

    [Fact]
    public async Task AvaliarEntradaAsync_TodosPermitindo_DevolveResultadoOk()
    {
        var g1 = new GuardRailDeTeste("g1", GuardRailFase.Entrada, GuardRailResultado.Ok);
        var g2 = new GuardRailDeTeste("g2", GuardRailFase.Entrada, GuardRailResultado.Ok);
        var pipeline = new GuardRailPipeline([g1, g2], new FakeLogger<GuardRailPipeline>());

        var resultado = await pipeline.AvaliarEntradaAsync(new GuardRailContexto("chat1", "pergunta"));

        Assert.True(resultado.Permitido);
        Assert.Single(g1.Chamadas);
        Assert.Single(g2.Chamadas); // os dois rodaram, nenhum bloqueou
    }
}
