using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace Giom.VideoManager.Infrastructure.Logging;

/// <summary>
/// Envia logs de erro para o Sentry (captura automática de exceção + rastreamento) quando
/// Sentry:Dsn estiver configurado — o DSN vem do SSM (secreto), nunca de appsettings.json em
/// texto puro. Sem DSN configurado, é um no-op — nenhuma mudança de comportamento.
/// </summary>
public static class SerilogSentryExtensions
{
    public static LoggerConfiguration AddSentryIfConfigured(this LoggerConfiguration loggerConfiguration, IConfiguration config, string environment)
    {
        var dsn = config["Sentry:Dsn"];
        if (string.IsNullOrWhiteSpace(dsn))
            return loggerConfiguration;

        return loggerConfiguration.WriteTo.Sentry(o =>
        {
            o.Dsn = dsn;
            o.Environment = environment;
            // Breadcrumbs em Information dão contexto (últimas linhas de log) em cada evento
            // de erro; só Error+ vira evento de fato — não queremos volume normal de logs lá.
            o.MinimumBreadcrumbLevel = LogEventLevel.Information;
            o.MinimumEventLevel = LogEventLevel.Error;
        });
    }
}
