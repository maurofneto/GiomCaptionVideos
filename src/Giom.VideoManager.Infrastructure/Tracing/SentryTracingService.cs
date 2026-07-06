using Giom.VideoManager.Domain.Interfaces;
using Sentry;

namespace Giom.VideoManager.Infrastructure.Tracing;

/// <summary>
/// Seguro mesmo se o Sentry não estiver inicializado nesse worker (Sentry:Dsn vazio ou
/// tracesSampleRate não configurado) — a SentrySdk vira no-op nesse caso, sem lançar exceção.
/// </summary>
public class SentryTracingService : ITracingService
{
    public ITrace IniciarTrace(string nome, string operacao)
    {
        var transacao = SentrySdk.StartTransaction(nome, operacao);
        SentrySdk.ConfigureScope(scope => scope.Transaction = transacao);
        return new SentryTrace(transacao);
    }

    private sealed class SentryTrace(ITransactionTracer transacao) : ITrace
    {
        private bool _finalizado;

        public IDisposable IniciarEtapa(string nome) => transacao.StartChild(nome);

        public void MarcarErro(Exception ex)
        {
            transacao.Finish(ex);
            _finalizado = true;
        }

        public void Dispose()
        {
            if (!_finalizado) transacao.Finish();
        }
    }
}
