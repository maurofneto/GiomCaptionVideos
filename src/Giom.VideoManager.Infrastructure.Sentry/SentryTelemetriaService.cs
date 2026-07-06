using Giom.VideoManager.Domain.Interfaces;
using Sentry;

namespace Giom.VideoManager.Infrastructure.Sentry;

/// <summary>
/// Não cria evento novo no Sentry pra cada chamada (viraria ruído/custo desnecessário no
/// Sentry, que cobra por evento) — só anexa como breadcrumb no escopo atual. Se um erro real
/// acontecer depois na mesma operação e for capturado pelo Sentry, o relatório já vem com o
/// histórico de operações de IA que aconteceram antes (ex: "esse resumo gastou 1200 tokens
/// antes de falhar na categorização").
/// Além do breadcrumb, anexa tempo/tokens/custo como measurement na span ativa no momento
/// (SentrySdk.GetSpan — resolve pra span/etapa mais recente ainda aberta, ou a transação como
/// fallback) — measurements de uma transação são consultáveis via Discover/Dashboards no
/// Sentry, diferente de breadcrumb (que só aparece atrelado a um evento de erro especifico).
/// Nome prefixado com o Tipo (ex.: "resumo.custo_usd") pra não colidir quando mais de um tipo
/// de operação contribui pra mesma span/transação (ex.: um vídeo com resumo + categorização).
/// Seguro mesmo se o Sentry não estiver inicializado nesse worker (Sentry:Dsn vazio) —
/// SentrySdk vira no-op nesse caso.
/// </summary>
public class SentryTelemetriaService : ITelemetriaService
{
    public Task RegistrarAsync(RegistroTelemetria registro, CancellationToken ct = default)
    {
        var dados = new Dictionary<string, string> { ["entidadeId"] = registro.EntidadeId };
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

        var span = SentrySdk.GetSpan();
        if (span is not null)
        {
            span.SetMeasurement($"{registro.Tipo}.tempo_ms", registro.TempoMs, MeasurementUnit.Duration.Millisecond);
            if (registro.TokensEntrada is not null)
                span.SetMeasurement($"{registro.Tipo}.tokens_entrada", registro.TokensEntrada.Value, MeasurementUnit.Custom("tokens"));
            if (registro.TokensSaida is not null)
                span.SetMeasurement($"{registro.Tipo}.tokens_saida", registro.TokensSaida.Value, MeasurementUnit.Custom("tokens"));
            if (registro.CustoEstimadoUsd is not null)
                span.SetMeasurement($"{registro.Tipo}.custo_usd", (double)registro.CustoEstimadoUsd.Value, MeasurementUnit.Custom("usd"));

            span.SetTag($"{registro.Tipo}.sucesso", registro.Sucesso.ToString());
            if (registro.Provedor is not null) span.SetTag($"{registro.Tipo}.provedor", registro.Provedor);
            if (registro.Modelo is not null) span.SetTag($"{registro.Tipo}.modelo", registro.Modelo);
        }

        return Task.CompletedTask;
    }
}
