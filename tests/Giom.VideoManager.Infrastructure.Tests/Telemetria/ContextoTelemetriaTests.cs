using Giom.VideoManager.Infrastructure.Telemetria;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Telemetria;

/// <summary>
/// ContextoTelemetria é o canal AsyncLocal compartilhado entre um decorator Telemetria* e a
/// implementação real que ele envolve — o comportamento de IniciarEscopo/Registrar aqui é o
/// que faz (ou quebra) a coleta de tokens/custo em toda a aplicação, então merece cobertura
/// direta, sem passar por nenhum decorator específico.
/// </summary>
public class ContextoTelemetriaTests
{
    [Fact]
    public void IniciarEscopo_SemReaproveitar_SempreDevolveObjetoNovo()
    {
        var contexto = new ContextoTelemetria();

        var primeiro = contexto.IniciarEscopo();
        var segundo = contexto.IniciarEscopo();

        Assert.NotSame(primeiro, segundo);
    }

    [Fact]
    public void IniciarEscopo_ReaproveitandoSemEscopoAberto_CriaUmNovo()
    {
        var contexto = new ContextoTelemetria();

        var escopo = contexto.IniciarEscopo(reaproveitarEscopoExistente: true);

        Assert.NotNull(escopo);
    }

    [Fact]
    public void IniciarEscopo_ReaproveitandoComEscopoJaAberto_DevolveOMesmoObjeto()
    {
        var contexto = new ContextoTelemetria();

        var externo = contexto.IniciarEscopo(reaproveitarEscopoExistente: true);
        var interno = contexto.IniciarEscopo(reaproveitarEscopoExistente: true);

        Assert.Same(externo, interno);
    }

    [Fact]
    public void Registrar_ForaDeQualquerEscopo_NaoLancaExcecao()
    {
        var contexto = new ContextoTelemetria();

        var excecao = Record.Exception(() => contexto.Registrar(provedor: "Claude", modelo: "sonnet-5"));

        Assert.Null(excecao);
    }

    [Fact]
    public void Registrar_DentroDeUmEscopo_PreencheOObjetoQuemAbriuOEscopoEnxerga()
    {
        var contexto = new ContextoTelemetria();
        var escopo = contexto.IniciarEscopo();

        contexto.Registrar(provedor: "Claude", modelo: "sonnet-5", tokensEntrada: 100, tokensSaida: 50, custoEstimadoUsd: 0.01m, requestIdExterno: "req-123");

        Assert.Equal("Claude", escopo.Provedor);
        Assert.Equal("sonnet-5", escopo.Modelo);
        Assert.Equal(100, escopo.TokensEntrada);
        Assert.Equal(50, escopo.TokensSaida);
        Assert.Equal(0.01m, escopo.CustoEstimadoUsd);
        Assert.Equal("req-123", escopo.RequestIdExterno);
    }

    [Fact]
    public async Task IniciarEscopo_ReaproveitandoAtravesDeUmAwait_ContinuaVendoAsMutacoesDoChamadoInterno()
    {
        // Simula o padrão real: ChatBotProcessor abre o escopo, chama (via await) o decorator
        // que decide reaproveitar — a mutação feita DEPOIS do await precisa ficar visível pro
        // chamador externo sem ele precisar reler o AsyncLocal.
        var contexto = new ContextoTelemetria();
        var escopoExterno = contexto.IniciarEscopo();

        await ChamarDecoratorQueReaproveitaERegistra(contexto);

        Assert.Equal("Claude", escopoExterno.Provedor);
        Assert.Equal(999, escopoExterno.TokensEntrada);
    }

    private static async Task ChamarDecoratorQueReaproveitaERegistra(ContextoTelemetria contexto)
    {
        await Task.Yield(); // força passar por um ponto de suspensão de verdade
        var escopoInterno = contexto.IniciarEscopo(reaproveitarEscopoExistente: true);
        contexto.Registrar(provedor: "Claude", tokensEntrada: 999);
        _ = escopoInterno;
    }

    [Fact]
    public async Task EscoposDeFluxosAssincronosDiferentes_NaoSeMisturam()
    {
        // Dois "flows" concorrentes usando a MESMA instância de ContextoTelemetria (como
        // acontece na aplicação real, onde ContextoTelemetria é singleton) não deveriam
        // enxergar o escopo um do outro — é isso que a semântica de AsyncLocal garante.
        var contexto = new ContextoTelemetria();

        var tarefaA = Task.Run(async () =>
        {
            var escopo = contexto.IniciarEscopo();
            await Task.Delay(30);
            contexto.Registrar(provedor: "ProvedorA");
            return escopo;
        });

        var tarefaB = Task.Run(async () =>
        {
            var escopo = contexto.IniciarEscopo();
            await Task.Delay(10);
            contexto.Registrar(provedor: "ProvedorB");
            return escopo;
        });

        var escopoA = await tarefaA;
        var escopoB = await tarefaB;

        Assert.Equal("ProvedorA", escopoA.Provedor);
        Assert.Equal("ProvedorB", escopoB.Provedor);
    }
}
