using Serilog.Core;
using Serilog.Events;

namespace Giom.VideoManager.Infrastructure.Logging;

/// <summary>
/// SourceContext do Serilog vem com o namespace inteiro (ex: "Giom.VideoManager.Application.Pipeline.ChatBotProcessor"),
/// poluindo a linha de log. Adiciona ShortSourceContext só com o nome da classe, pra usar no
/// outputTemplate como um identificador curto (ex: "[ChatBotProcessor]").
/// </summary>
public class ShortSourceContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var value) &&
            value is ScalarValue { Value: string fullName })
        {
            var curto = fullName[(fullName.LastIndexOf('.') + 1)..];
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ShortSourceContext", curto));
        }
    }
}
