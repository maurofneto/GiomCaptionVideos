using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Telemetria;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Telemetria;

public class AlertaGastoTelemetriaServiceTests
{
    private static (AlertaGastoTelemetriaService Servico, FakeLogger<AlertaGastoTelemetriaService> Logger, FakeTimeProvider Tempo) Criar(bool habilitado, decimal limite)
    {
        var tempo = new FakeTimeProvider();
        var logger = new FakeLogger<AlertaGastoTelemetriaService>();
        var servico = new AlertaGastoTelemetriaService(Options.Create(new AlertaGastoOptions { Habilitado = habilitado, LimiteDiarioUsd = limite }), logger, tempo);
        return (servico, logger, tempo);
    }

    private static RegistroTelemetria RegistroComCusto(decimal? custo) => new("bot_interacao", "1", 100, true, CustoEstimadoUsd: custo);

    [Fact]
    public async Task RegistrarAsync_QuandoDesabilitado_NuncaLogaMesmoAcimaDoLimite()
    {
        var (servico, logger, _) = Criar(habilitado: false, limite: 1.0m);

        await servico.RegistrarAsync(RegistroComCusto(10m));

        Assert.Empty(logger.Entradas);
    }

    [Fact]
    public async Task RegistrarAsync_SemCustoNoRegistro_Ignora()
    {
        var (servico, logger, _) = Criar(habilitado: true, limite: 1.0m);

        await servico.RegistrarAsync(RegistroComCusto(null));

        Assert.Empty(logger.Entradas);
    }

    [Fact]
    public async Task RegistrarAsync_AbaixoDoLimite_NaoDisparaAlerta()
    {
        var (servico, logger, _) = Criar(habilitado: true, limite: 10.0m);

        await servico.RegistrarAsync(RegistroComCusto(3m));
        await servico.RegistrarAsync(RegistroComCusto(3m));

        Assert.Empty(logger.Entradas);
    }

    [Fact]
    public async Task RegistrarAsync_AoAcumularAcimaDoLimite_DisparaCriticalUmaUnicaVez()
    {
        var (servico, logger, _) = Criar(habilitado: true, limite: 5.0m);

        await servico.RegistrarAsync(RegistroComCusto(3m)); // acumulado: 3
        await servico.RegistrarAsync(RegistroComCusto(3m)); // acumulado: 6 — passou de 5
        await servico.RegistrarAsync(RegistroComCusto(3m)); // acumulado: 9 — já disparou, não repete

        Assert.Single(logger.Entradas, e => e.Nivel == LogLevel.Critical);
    }

    [Fact]
    public async Task RegistrarAsync_QuandoODiaVira_ZeraOAcumuladoEPodeAlertarDeNovo()
    {
        var (servico, logger, tempo) = Criar(habilitado: true, limite: 5.0m);

        await servico.RegistrarAsync(RegistroComCusto(6m)); // dia 1 — dispara
        Assert.Single(logger.Entradas, e => e.Nivel == LogLevel.Critical);

        tempo.Advance(TimeSpan.FromDays(1)); // vira o dia (UTC)

        await servico.RegistrarAsync(RegistroComCusto(6m)); // dia 2 — acumulado reseta e dispara de novo

        Assert.Equal(2, logger.Entradas.Count(e => e.Nivel == LogLevel.Critical));
    }
}
