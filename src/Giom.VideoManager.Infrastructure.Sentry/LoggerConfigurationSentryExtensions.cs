using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace Giom.VideoManager.Infrastructure.Sentry;

/// <summary>
/// Só liga o SentryLogEventSink (encaminha log -> Sentry) — quem de fato inicializa o SentrySdk
/// e o TracerProvider é SentryBootstrap.Initialize, chamado em AddSentryTracing (ConfigureServices).
/// Ver SentryLogEventSink pro motivo de não usar mais o pacote oficial Sentry.Serilog aqui.
/// </summary>
public static class LoggerConfigurationSentryExtensions
{
    public static LoggerConfiguration AddSentrySink(this LoggerConfiguration loggerConfiguration, IConfiguration config)
    {
        var minimumEventLevel = config.GetValue("Sentry:MinimumEventLevel", LogEventLevel.Error);
        return loggerConfiguration.WriteTo.Sink(new SentryLogEventSink(minimumEventLevel));
    }
}
