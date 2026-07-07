using Giom.VideoManager.Infrastructure.Observabilidade;
using Serilog.Core;
using Serilog.Events;

namespace Giom.VideoManager.Infrastructure.Logging;

/// <summary>
/// Enriquece todo log com o CorrelationId (SessionId) e as Propriedades do ISessionContext
/// ambiente atual — mesma fonte que o SentryTracingService usa pra taguear Spans, e que o
/// HeliconeHeadersHandler usa pra montar os headers. Um lugar só (ChannelProcessor.
/// DefinirSessaoAtual, ou equivalente) define os atributos; Sentry, Helicone e log leem dali,
/// em vez de cada um receber uma cópia manual (BeginScope/DefinirTag) dos mesmos valores.
/// Referencia SessionContext diretamente (não via DI) porque o AsyncLocal por trás dela é
/// estático — funciona igual independente de quem instanciou, e o pipeline do Serilog é montado
/// antes do container de DI existir.
/// AddPropertyIfAbsent nunca sobrescreve uma propriedade já definida por um BeginScope mais
/// específico (ex.: o CorrelationId próprio do bot) — só preenche o que ainda não foi definido.
/// </summary>
public class SessionContextEnricher : ILogEventEnricher
{
    private readonly SessionContext _sessionContext = new();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var sessao = _sessionContext.Atual;
        if (sessao is null) return;

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CorrelationId", sessao.SessionId));

        if (sessao.Propriedades is null) return;
        foreach (var (chave, valor) in sessao.Propriedades)
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(chave, valor));
    }
}
