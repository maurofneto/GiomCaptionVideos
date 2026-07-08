using System.Reflection;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Trace;
using Sentry.OpenTelemetry;

namespace Giom.VideoManager.Infrastructure.Sentry;

/// <summary>
/// Inicializa o SentrySdk e SÓ DEPOIS constrói o TracerProvider do OpenTelemetry — na prática, a
/// ordem inversa da que a documentação do Sentry sugere num exemplo simplificado de console app.
/// Confirmado lendo o código-fonte real (Sentry.OpenTelemetry.TracerProviderBuilderExtensions.AddSentry):
/// o processor é criado por uma factory ADIADA até TracerProviderBuilder.Build() rodar, e essa
/// factory checa hub.IsEnabled NAQUELE INSTANTE — se o SentrySdk.Init ainda não rodou, ela devolve
/// um processor no-op PERMANENTE (não reavalia depois, mesmo que o Init rode em seguida). Foi
/// exatamente isso que causou "zero spans chegando" quando esse código seguia a ordem oficial
/// (Build antes de Init) — confirmado via Sentry MCP: nem transações isoladas apareciam mais.
/// Com Init primeiro, hub.IsEnabled já é true quando o Build() roda, e o SentrySpanProcessor real
/// é criado com o hub certo.
/// </summary>
public static class SentryBootstrap
{
    public static IDisposable Initialize(IConfiguration config)
    {
        var dsn = config["Sentry:Dsn"];
        if (string.IsNullOrWhiteSpace(dsn)) return new NoOpDisposable();

        var buildId = config["BUILD_ID"];
        var nomeApp = Assembly.GetEntryAssembly()?.GetName().Name ?? "giom";

        var sentry = global::Sentry.SentrySdk.Init(o =>
        {
            o.Dsn = dsn;
            o.Environment = config["Sentry:Environment"];
            o.Release = string.IsNullOrWhiteSpace(buildId) ? null : $"{nomeApp}@{buildId}";
            o.TracesSampleRate = config.GetValue<double?>("Sentry:TracesSampleRate");
            // Habilitado aqui também (não só no sink) — cobre spans/metrics emitidos fora do
            // caminho de log (ex.: SentryTelemetriaService.RegistrarAsync).
            o.EnableLogs = config.GetValue("Sentry:EnableLogs", true);
            o.EnableMetrics = config.GetValue("Sentry:EnableMetrics", true);
            // Desliga a instrumentação nativa do Sentry (SentrySdk.StartTransaction) em favor do
            // Activity do OpenTelemetry — o TracerProvider construído abaixo é quem gera
            // Transaction/Span a partir de agora.
            o.UseOpenTelemetry();
        });

        var tracerProvider = global::OpenTelemetry.Sdk.CreateTracerProviderBuilder()
            .AddSource(GiomActivitySource.Name)
            .AddSentry()
            .Build();

        return new CompositeDisposable(tracerProvider, sentry);
    }

    private sealed class CompositeDisposable(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
