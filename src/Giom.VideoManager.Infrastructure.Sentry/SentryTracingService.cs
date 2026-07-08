using System.Diagnostics;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Sentry;

namespace Giom.VideoManager.Infrastructure.Sentry;

/// <summary>
/// Abre Activity (System.Diagnostics — ver GiomActivitySource) em vez de chamar a API do Sentry
/// diretamente; quem traduz Activity em Transaction/Span do Sentry é o
/// Sentry.OpenTelemetry.SentrySpanProcessor, plugado via AddSentryTracing. ITracingService/ITrace
/// continuam idênticos pra quem consome (ChannelProcessor/ChatBotProcessor) — só a implementação
/// trocou de "SentrySdk.StartTransaction direto" pra "Activity nativo do .NET", o que também é o
/// que permite qualquer etapa da pipeline (resumo/categorização/embedding) abrir seu próprio Span
/// sem precisar receber o trace por parâmetro (Activity.Current é ambient).
/// Todo Activity criado aqui (trace novo OU etapa) já sai tagueado com o que estiver em
/// ISessionContext.Atual (CorrelationId + Propriedades) — é o que elimina a necessidade de quem
/// chama (ChannelProcessor, etc.) ficar repetindo DefinirTag/BeginScope manualmente pros mesmos
/// atributos: define uma vez no SessionContext, aparece em todo Span daquele ponto em diante.
/// Seguro mesmo sem listener registrado (Sentry:Dsn vazio) — ActivitySource.StartActivity só
/// devolve null nesse caso, e todo método aqui já trata isso.
/// Todo trace/etapa aberto via IniciarTrace/ITrace.IniciarEtapa também emite uma métrica
/// "giom.tempo_ms" (mesmo nome que SentryTelemetriaService já usa pra resumo/categorização/
/// transcrição/embedding) — assim os grupos da pipeline (canal/playlist/página/vídeo) e as
/// etapas do bot (guardrail/busca_rag/agente/envio) entram na MESMA série temporal, filtrável
/// por tag "tipo", em vez de precisar abrir uma transação específica no Sentry pra ver quanto
/// tempo cada nível está levando. IniciarEtapaAtual NÃO ganha esse tratamento — resumo/
/// categorização/transcrição/embedding já emitem essa métrica pelo caminho de
/// ITelemetriaService/RegistroTelemetria, e duplicaria a série se emitíssemos aqui também.
/// </summary>
public class SentryTracingService(ISessionContext sessionContext) : ITracingService
{
    public ITrace IniciarTrace(string nome, string operacao, string? contextoExterno = null)
    {
        var activity = contextoExterno is not null && ActivityContext.TryParse(contextoExterno, null, out var contexto)
            ? GiomActivitySource.Instance.StartActivity(nome, ActivityKind.Internal, contexto)
            : GiomActivitySource.Instance.StartActivity(nome);

        activity?.SetTag("operacao", operacao);
        AplicarAtributosDaSessao(activity);
        return new SentryTrace(activity, this);
    }

    public IDisposable IniciarEtapaAtual(string nome)
    {
        var activity = GiomActivitySource.Instance.StartActivity(nome);
        AplicarAtributosDaSessao(activity);
        return (IDisposable?)activity ?? NoopDisposable.Instance;
    }

    public string? CapturarContextoAtual() => Activity.Current?.Id;

    private void AplicarAtributosDaSessao(Activity? activity)
    {
        if (activity is null) return;

        var sessao = sessionContext.Atual;
        if (sessao is null) return;

        activity.SetTag("correlation.id", sessao.SessionId);
        if (sessao.Propriedades is null) return;
        foreach (var (chave, valor) in sessao.Propriedades)
            activity.SetTag(chave, valor);
    }

    private void EmitirMetricaDeDuracao(Activity? activity)
    {
        if (activity is null) return;

        var atributos = new List<KeyValuePair<string, object>>
        {
            new("tipo", activity.OperationName),
            new("sucesso", activity.Status != ActivityStatusCode.Error),
        };

        var sessao = sessionContext.Atual;
        if (sessao is not null)
        {
            atributos.Add(new("correlation_id", sessao.SessionId));
            if (sessao.Propriedades is not null)
                foreach (var (chave, valor) in sessao.Propriedades)
                    atributos.Add(new(chave, valor));
        }

        SentrySdk.Metrics.EmitDistribution("giom.tempo_ms", activity.Duration.TotalMilliseconds, MeasurementUnit.Duration.Millisecond, atributos);
    }

    private sealed class SentryTrace(Activity? activity, SentryTracingService owner) : ITrace
    {
        public IDisposable IniciarEtapa(string nome)
        {
            var filho = GiomActivitySource.Instance.StartActivity(nome);
            owner.AplicarAtributosDaSessao(filho);
            return filho is null ? NoopDisposable.Instance : new EtapaComMetrica(filho, owner);
        }

        public void MarcarErro(Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
        }

        public void DefinirTag(string chave, string valor) => activity?.SetTag(chave, valor);

        public void Dispose()
        {
            activity?.Dispose();
            owner.EmitirMetricaDeDuracao(activity);
        }
    }

    private sealed class EtapaComMetrica(Activity activity, SentryTracingService owner) : IDisposable
    {
        public void Dispose()
        {
            activity.Dispose();
            owner.EmitirMetricaDeDuracao(activity);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
