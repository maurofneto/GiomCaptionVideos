using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Telemetria;
using Giom.VideoManager.Testes.Comuns;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Telemetria;

file sealed class SinkQueSempreFalha : ITelemetriaService
{
    public Task RegistrarAsync(RegistroTelemetria registro, CancellationToken ct = default) =>
        throw new InvalidOperationException("Sink indisponível");
}

public class CompositeTelemetriaServiceTests
{
    private static RegistroTelemetria RegistroDeExemplo() => new("teste", "entidade-1", 100, true);

    [Fact]
    public async Task RegistrarAsync_DistribuiParaTodosOsSinksConfigurados()
    {
        var sink1 = new FakeTelemetriaService();
        var sink2 = new FakeTelemetriaService();
        var composite = new CompositeTelemetriaService([sink1, sink2], new FakeLogger<CompositeTelemetriaService>());

        await composite.RegistrarAsync(RegistroDeExemplo());

        Assert.Single(sink1.Registros);
        Assert.Single(sink2.Registros);
    }

    [Fact]
    public async Task RegistrarAsync_QuandoUmSinkFalha_OsDemaisAindaRecebemORegistro()
    {
        var sinkOk = new FakeTelemetriaService();
        var composite = new CompositeTelemetriaService([new SinkQueSempreFalha(), sinkOk], new FakeLogger<CompositeTelemetriaService>());

        var excecao = await Record.ExceptionAsync(() => composite.RegistrarAsync(RegistroDeExemplo()));

        Assert.Null(excecao); // a falha de um sink nunca deveria propagar pro chamador
        Assert.Single(sinkOk.Registros); // o sink saudável recebeu normalmente
    }

    [Fact]
    public async Task RegistrarAsync_QuandoUmSinkFalha_LogaOAvisoComONomeDoSink()
    {
        var logger = new FakeLogger<CompositeTelemetriaService>();
        var composite = new CompositeTelemetriaService([new SinkQueSempreFalha()], logger);

        await composite.RegistrarAsync(RegistroDeExemplo());

        Assert.Contains(logger.Entradas, e => e.Mensagem.Contains(nameof(SinkQueSempreFalha)));
    }

    [Fact]
    public async Task RegistrarAsync_SemNenhumSink_NaoLancaExcecao()
    {
        var composite = new CompositeTelemetriaService([], new FakeLogger<CompositeTelemetriaService>());

        var excecao = await Record.ExceptionAsync(() => composite.RegistrarAsync(RegistroDeExemplo()));

        Assert.Null(excecao);
    }
}
