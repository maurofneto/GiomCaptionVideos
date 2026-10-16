using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.YouTube;

/// <summary>
/// Bug #147 — invalid_grant nos dois canais terminava a task com sucesso e sem incidente no
/// Sentry (log em ConfigureServices, antes do SentrySdk.Init). O reporte agora roda em
/// StartedAsync, depois de todos os hosted services.
/// </summary>
public class ReporteFalhasCredencialCanaisTests
{
    private static CanalIngestaoConfig Canal(string id, string nome) =>
        new(id, null, nome, true, true, default, [], 10, 0, 0, nome.ToLowerInvariant());

    private sealed class LoggerCapturador<T> : ILogger<T>
    {
        public List<(LogLevel Nivel, Exception? Erro, string Mensagem)> Entradas { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entradas.Add((logLevel, exception, formatter(state, exception)));
    }

    [Fact]
    public void AddYouTubeCredenciaisPorCanal_SemClientId_DescartaCanalEChamaCallback()
    {
        var config = new ConfigurationBuilder().Build();
        var canais = new[] { Canal("UC1", "Instituto"), Canal("UC2", "Ministerio") };
        var falhas = new List<FalhaCredencialCanal>();

        var validos = new ServiceCollection().AddYouTubeCredenciaisPorCanal(
            config, canais, new LoggerCapturador<object>(), (c, e) => falhas.Add(new(c, e)));

        Assert.Empty(validos);
        Assert.Equal(["UC1", "UC2"], falhas.Select(f => f.Canal.ChannelId));
        Assert.All(falhas, f => Assert.IsType<CredencialCanalIndisponivelException>(f.Erro));
    }

    [Fact]
    public async Task StartedAsync_NenhumCanalValido_LogaErrorPorCanalECriticalFinal()
    {
        var logger = new LoggerCapturador<ReporteFalhasCredencialCanais>();
        var erro = new InvalidOperationException("invalid_grant");
        var reporte = new ReporteFalhasCredencialCanais(
            [new(Canal("UC1", "Instituto"), erro), new(Canal("UC2", "Ministerio"), erro)], 2, 0, logger);

        await reporte.StartedAsync(CancellationToken.None);

        Assert.Equal(2, logger.Entradas.Count(e => e.Nivel == LogLevel.Error && ReferenceEquals(e.Erro, erro)));
        Assert.Single(logger.Entradas, e => e.Nivel == LogLevel.Critical);
    }

    [Fact]
    public async Task StartedAsync_FalhaParcial_LogaErrorSemCritical()
    {
        var logger = new LoggerCapturador<ReporteFalhasCredencialCanais>();
        var reporte = new ReporteFalhasCredencialCanais(
            [new(Canal("UC2", "Ministerio"), new InvalidOperationException("x"))], 2, 1, logger);

        await reporte.StartedAsync(CancellationToken.None);

        Assert.Single(logger.Entradas, e => e.Nivel == LogLevel.Error);
        Assert.DoesNotContain(logger.Entradas, e => e.Nivel == LogLevel.Critical);
    }

    [Fact]
    public void AddReporteFalhasCredencialCanais_SemFalhas_NaoRegistraHostedService()
    {
        var services = new ServiceCollection().AddReporteFalhasCredencialCanais([], 2, 2);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddReporteFalhasCredencialCanais_ComFalhas_RegistraHostedLifecycleService()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddReporteFalhasCredencialCanais([new(Canal("UC1", "Instituto"), new Exception("x"))], 1, 0);

        using var sp = services.BuildServiceProvider();

        Assert.IsType<ReporteFalhasCredencialCanais>(Assert.Single(sp.GetServices<IHostedService>()));
    }
}
