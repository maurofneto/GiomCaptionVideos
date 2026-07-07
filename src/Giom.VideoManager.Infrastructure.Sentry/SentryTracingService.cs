using System.Diagnostics;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Observabilidade;

namespace Giom.VideoManager.Infrastructure.Sentry;

/// <summary>
/// Abre Activity (System.Diagnostics — ver GiomActivitySource) em vez de chamar a API do Sentry
/// diretamente; quem traduz Activity em Transaction/Span do Sentry é o
/// Sentry.OpenTelemetry.SentrySpanProcessor, plugado via AddSentryTracing. ITracingService/ITrace
/// continuam idênticos pra quem consome (ChannelProcessor/ChatBotProcessor) — só a implementação
/// trocou de "SentrySdk.StartTransaction direto" pra "Activity nativo do .NET", o que também é o
/// que permite qualquer etapa da pipeline (resumo/categorização/embedding) abrir seu próprio Span
/// sem precisar receber o trace por parâmetro (Activity.Current é ambient).
/// Seguro mesmo sem listener registrado (Sentry:Dsn vazio) — ActivitySource.StartActivity só
/// devolve null nesse caso, e todo método aqui já trata isso.
/// </summary>
public class SentryTracingService : ITracingService
{
    public ITrace IniciarTrace(string nome, string operacao)
    {
        var activity = GiomActivitySource.Instance.StartActivity(nome);
        activity?.SetTag("operacao", operacao);
        return new SentryTrace(activity);
    }

    private sealed class SentryTrace(Activity? activity) : ITrace
    {
        public IDisposable IniciarEtapa(string nome)
            => (IDisposable?)GiomActivitySource.Instance.StartActivity(nome) ?? NoopDisposable.Instance;

        public void MarcarErro(Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
        }

        public void DefinirTag(string chave, string valor) => activity?.SetTag(chave, valor);

        public void Dispose() => activity?.Dispose();

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
