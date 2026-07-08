using Giom.VideoManager.Domain.Interfaces;
using Sentry;

namespace Giom.VideoManager.Infrastructure.Sentry;

/// <summary>
/// Não cria evento novo no Sentry pra cada chamada (viraria ruído/custo desnecessário no
/// Sentry, que cobra por evento) — só anexa como breadcrumb no escopo atual. Se um erro real
/// acontecer depois na mesma operação e for capturado pelo Sentry, o relatório já vem com o
/// histórico de operações de IA que aconteceram antes (ex: "esse resumo gastou 1200 tokens
/// antes de falhar na categorização").
/// Além do breadcrumb, anexa tempo/tokens/custo como measurement na TRANSAÇÃO ATUAL (não na
/// span/etapa filha) — confirmado por inspeção direta do payload de um evento real que
/// measurements anexadas a uma span filha (via SentrySdk.GetSpan()) chegam no Sentry mas não
/// ficam pesquisáveis no Discover/Dashboards. Como cada vídeo/interação agora abre sua PRÓPRIA
/// transação (ver ChannelProcessor/ChatBotProcessor), essa medição não se mistura mais com a de
/// outro vídeo/interação da mesma página/run — antes, quando a transação cobria a página
/// inteira, cada SetMeasurement sobrescrevia o valor do vídeo anterior com a mesma chave, e o
/// dashboard acabava mostrando só o último vídeo processado (parecendo "tempo igual pra tudo").
/// Nome prefixado com o Tipo (ex.: "resumo.custo_usd") pra não colidir quando mais de um tipo de
/// operação contribui pra mesma transação.
/// Também emite Sentry Metrics (distribution) com o mesmo dado, tagueado por tipo/provedor/
/// modelo/sucesso — diferente da measurement (presa a UMA transação), a métrica agrega em série
/// temporal entre transações/execuções diferentes (ex.: "tempo médio de resumo nos últimos 7
/// dias", sem precisar abrir uma transação específica pra consultar).
/// Seguro mesmo se o Sentry não estiver inicializado nesse worker (Sentry:Dsn vazio) —
/// SentrySdk vira no-op nesse caso.
/// US #89: cogitou-se migrar pra Meter/Histogram/Counter padrão do
/// System.Diagnostics.Metrics (OTel), exportado via Sentry.OpenTelemetry, em vez de
/// SentrySdk.Metrics.EmitDistribution (API própria do Sentry). Verificado por inspeção do
/// assembly `Sentry.OpenTelemetry` 6.6.0 (nenhuma referência a `MeterProviderBuilder`,
/// só `TracerProviderBuilder`) — essa versão NÃO exporta métricas OTel pro Sentry, só traces.
/// Mantido `SentrySdk.Metrics` até uma versão futura do pacote adicionar esse suporte.
/// </summary>
public class SentryTelemetriaService(ISessionContext sessionContext) : ITelemetriaSink
{
    public Task RegistrarAsync(RegistroTelemetria registro, CancellationToken ct = default)
    {
        // Mesmo Guid usado como Session-Id no Helicone e como CorrelationId nos logs (ver
        // ChannelProcessor) — lido daqui (em vez de recriado) pra garantir que os três
        // continuam apontando pra exatamente a mesma execução.
        var correlationId = sessionContext.Atual?.SessionId;

        var dados = new Dictionary<string, string> { ["entidadeId"] = registro.EntidadeId };
        if (correlationId is not null) dados["correlationId"] = correlationId;
        if (registro.Provedor is not null) dados["provedor"] = registro.Provedor;
        if (registro.Modelo is not null) dados["modelo"] = registro.Modelo;
        if (registro.TokensEntrada is not null) dados["tokensEntrada"] = registro.TokensEntrada.Value.ToString();
        if (registro.TokensSaida is not null) dados["tokensSaida"] = registro.TokensSaida.Value.ToString();
        if (registro.CustoEstimadoUsd is not null) dados["custoEstimadoUsd"] = registro.CustoEstimadoUsd.Value.ToString("F6");

        SentrySdk.AddBreadcrumb(
            message: $"{registro.Tipo} ({(registro.Sucesso ? "sucesso" : "falha")}, {registro.TempoMs}ms)",
            category: $"telemetria.{registro.Tipo}",
            type: "default",
            data: dados,
            level: registro.Sucesso ? BreadcrumbLevel.Info : BreadcrumbLevel.Warning);

        ITransactionTracer? transacao = null;
        SentrySdk.ConfigureScope(scope => transacao = scope.Transaction);

        if (transacao is not null)
        {
            transacao.SetMeasurement($"{registro.Tipo}.tempo_ms", registro.TempoMs, MeasurementUnit.Duration.Millisecond);
            if (registro.TokensEntrada is not null)
                transacao.SetMeasurement($"{registro.Tipo}.tokens_entrada", registro.TokensEntrada.Value, MeasurementUnit.Custom("tokens"));
            if (registro.TokensSaida is not null)
                transacao.SetMeasurement($"{registro.Tipo}.tokens_saida", registro.TokensSaida.Value, MeasurementUnit.Custom("tokens"));
            if (registro.CustoEstimadoUsd is not null)
                transacao.SetMeasurement($"{registro.Tipo}.custo_usd", (double)registro.CustoEstimadoUsd.Value, MeasurementUnit.Custom("usd"));

            transacao.SetTag($"{registro.Tipo}.sucesso", registro.Sucesso.ToString());
            if (registro.Provedor is not null) transacao.SetTag($"{registro.Tipo}.provedor", registro.Provedor);
            if (registro.Modelo is not null) transacao.SetTag($"{registro.Tipo}.modelo", registro.Modelo);
        }

        IEnumerable<KeyValuePair<string, object>> atributos =
        [
            new("tipo", registro.Tipo),
            new("sucesso", registro.Sucesso),
            new("provedor", registro.Provedor ?? "n/d"),
            new("modelo", registro.Modelo ?? "n/d"),
            new("correlation_id", correlationId ?? "n/d"),
        ];

        SentrySdk.Metrics.EmitDistribution("giom.tempo_ms", registro.TempoMs, MeasurementUnit.Duration.Millisecond, atributos);
        if (registro.CustoEstimadoUsd is not null)
            SentrySdk.Metrics.EmitDistribution("giom.custo_usd", (double)registro.CustoEstimadoUsd.Value, MeasurementUnit.Custom("usd"), atributos);
        if (registro.TokensEntrada is not null)
            SentrySdk.Metrics.EmitDistribution("giom.tokens_entrada", registro.TokensEntrada.Value, MeasurementUnit.Custom("tokens"), atributos);
        if (registro.TokensSaida is not null)
            SentrySdk.Metrics.EmitDistribution("giom.tokens_saida", registro.TokensSaida.Value, MeasurementUnit.Custom("tokens"), atributos);

        return Task.CompletedTask;
    }
}
