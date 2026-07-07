using System.Reflection;
using Microsoft.Extensions.Configuration;
using Sentry.OpenTelemetry;
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

        // BUILD_ID vem do Dockerfile (ARG/ENV, preenchido pelo pipeline com $(Build.BuildId)) —
        // sem isso, o SDK só tinha a versão do assembly (que não muda a cada deploy) pra montar
        // o Release, e todo deploy aparecia como a mesma Release no Sentry.
        var buildId = config["BUILD_ID"];
        var nomeApp = Assembly.GetEntryAssembly()?.GetName().Name ?? "giom";

        return loggerConfiguration.WriteTo.Sentry(o =>
        {
            o.Dsn = dsn;
            o.Environment = config["Sentry:Environment"];
            o.Release = string.IsNullOrWhiteSpace(buildId) ? null : $"{nomeApp}@{buildId}";
            o.MinimumBreadcrumbLevel = config.GetValue("Sentry:MinimumBreadcrumbLevel", LogEventLevel.Information);
            o.MinimumEventLevel = config.GetValue("Sentry:MinimumEventLevel", LogEventLevel.Error);
            o.TracesSampleRate = config.GetValue<double?>("Sentry:TracesSampleRate");
            // Warnings/Errors passam a aparecer em Sentry > Logs (não só como Issue/Evento) —
            // e habilita SentrySdk.Metrics (série temporal agregável, usado em SentryTelemetriaService).
            o.EnableLogs = config.GetValue("Sentry:EnableLogs", true);
            o.EnableMetrics = config.GetValue("Sentry:EnableMetrics", true);

            // Desliga a instrumentação nativa do Sentry (SentrySdk.StartTransaction) em favor do
            // Activity do OpenTelemetry — GiomActivitySource + Sentry.OpenTelemetry.SentrySpanProcessor
            // (ligado em AddSentryTracing) é quem gera Transaction/Span a partir de agora.
            o.UseOpenTelemetry();
        });
    }
}
