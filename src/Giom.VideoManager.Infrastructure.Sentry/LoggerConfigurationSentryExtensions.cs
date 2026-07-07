using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace Giom.VideoManager.Infrastructure.Sentry;

/// <summary>
/// O overload de .WriteTo.Sentry(dsn, ...) usado pelo binding declarativo do
/// Serilog.Settings.Configuration (Serilog:WriteTo no appsettings.json) casa Args por nome de
/// parâmetro — e nenhum desses overloads tem um parâmetro "tracesSampleRate". Configurado via
/// JSON, esse valor é silenciosamente ignorado (sem erro, sem warning), deixando o Sentry
/// Performance sempre desligado mesmo com a chave presente no appsettings.
/// Por isso o sink é montado aqui, em código, via o overload Action&lt;SentryOptions&gt; — o
/// único que expõe TracesSampleRate — lendo os valores da seção "Sentry" do appsettings.json
/// (não mais de Serilog:WriteTo:Sentry:Args).
/// </summary>
public static class LoggerConfigurationSentryExtensions
{
    public static LoggerConfiguration ConfigureSentry(this LoggerConfiguration loggerConfiguration, IConfiguration config)
    {
        var dsn = config["Sentry:Dsn"];
        if (string.IsNullOrWhiteSpace(dsn))
            return loggerConfiguration;

        return loggerConfiguration.WriteTo.Sentry(o =>
        {
            o.Dsn = dsn;
            o.Environment = config["Sentry:Environment"];
            o.MinimumBreadcrumbLevel = config.GetValue("Sentry:MinimumBreadcrumbLevel", LogEventLevel.Information);
            o.MinimumEventLevel = config.GetValue("Sentry:MinimumEventLevel", LogEventLevel.Error);
            o.TracesSampleRate = config.GetValue<double?>("Sentry:TracesSampleRate");
        });
    }
}
