using Sentry;
using Serilog.Core;
using Serilog.Events;

namespace Giom.VideoManager.Infrastructure.Sentry;

/// <summary>
/// Encaminha eventos do Serilog pro SentrySdk já inicializado (ver SentryBootstrap) — nunca
/// chama SentrySdk.Init. É isso que separa "quando o Sentry inicializa" (controlado por
/// SentryBootstrap, na ordem que o OpenTelemetry exige) de "como o log chega lá" — o sink
/// oficial (Sentry.Serilog) fazia as duas coisas juntas, sem dar controle sobre a ordem.
/// Seguro mesmo se o Sentry ainda não tiver inicializado (ex.: logs muito cedo no boot do host,
/// antes de ConfigureServices rodar) — SentrySdk.IsEnabled fica false e Emit vira no-op.
/// Info/Warning/Error/etc. sempre viram Sentry Logs (aba Logs); Warning+ (MinimumEventLevel)
/// também vira um Evento/Issue de verdade, igual o sink oficial fazia.
/// </summary>
public class SentryLogEventSink(LogEventLevel minimumEventLevel, IFormatProvider? formatProvider = null) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        if (!SentrySdk.IsEnabled) return;

        var mensagem = logEvent.RenderMessage(formatProvider);

        void ConfigurarAtributos(SentryLog log)
        {
            foreach (var (chave, valor) in logEvent.Properties)
                log.SetAttribute(chave, valor is ScalarValue escalar ? escalar.Value ?? "" : valor.ToString());
        }

        switch (logEvent.Level)
        {
            case LogEventLevel.Verbose:     SentrySdk.Logger.LogTrace(ConfigurarAtributos, mensagem); break;
            case LogEventLevel.Debug:       SentrySdk.Logger.LogDebug(ConfigurarAtributos, mensagem); break;
            case LogEventLevel.Information: SentrySdk.Logger.LogInfo(ConfigurarAtributos, mensagem); break;
            case LogEventLevel.Warning:     SentrySdk.Logger.LogWarning(ConfigurarAtributos, mensagem); break;
            case LogEventLevel.Error:       SentrySdk.Logger.LogError(ConfigurarAtributos, mensagem); break;
            case LogEventLevel.Fatal:       SentrySdk.Logger.LogFatal(ConfigurarAtributos, mensagem); break;
        }

        if (logEvent.Level < minimumEventLevel) return;

        if (logEvent.Exception is not null)
            SentrySdk.CaptureException(logEvent.Exception, scope => scope.SetExtra("log_message", mensagem));
        else
            SentrySdk.CaptureMessage(mensagem, logEvent.Level switch
            {
                LogEventLevel.Fatal => SentryLevel.Fatal,
                LogEventLevel.Error => SentryLevel.Error,
                LogEventLevel.Warning => SentryLevel.Warning,
                _ => SentryLevel.Info
            });
    }
}
