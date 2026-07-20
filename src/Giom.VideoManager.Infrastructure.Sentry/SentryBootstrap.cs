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
    public static IDisposable Initialize(
        IConfiguration config,
        IEnumerable<ISentryTransactionExclusionRule>? regrasDeExclusao = null,
        IEnumerable<ISentryDataScrubber>? scrubbers = null)
    {
        var dsn = config["Sentry:Dsn"];
        if (string.IsNullOrWhiteSpace(dsn)) return new NoOpDisposable();

        var buildId = config["BUILD_ID"];
        var nomeApp = Assembly.GetEntryAssembly()?.GetName().Name ?? "giom";

        // Cópia em array: os IEnumerable vêm de DI (podem ser lazy) e o callback abaixo roda
        // repetidamente durante toda a vida do processo — não queremos reenumerar (ou pior,
        // reconstruir) a cada transação.
        var regras = (regrasDeExclusao ?? []).ToArray();
        var todosScrubbers = (scrubbers ?? []).ToArray();

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
            // Bug #128: a instrumentação automática de HttpClient nomeia a transação com a URL
            // inteira — ali podem morar dois problemas, ruído (chamada de baixo valor repetida
            // sem parar, ex.: o long-polling do Telegram) e segredo em texto puro (URL com token
            // embutido, ex.: api.telegram.org/bot<TOKEN>/...). Em vez de resolver caso a caso
            // aqui, cada adapter de Infrastructure.* que sabe da própria chamada ruidosa/sensível
            // contribui sua regra via DI (ISentryTransactionExclusionRule/ISentryDataScrubber,
            // ver Giom.VideoManager.Infrastructure.Observabilidade) — este bootstrap só agrega.
            // SentryTransaction.Name já não aceita mais escrita nesta versão do SDK (só a
            // exclusão é feita aqui) — a redação de dado sensível é feita mais abaixo, direto na
            // Activity do OpenTelemetry (EnrichWithHttpRequestMessage), antes da transação existir.
            o.SetBeforeSendTransaction(transaction =>
                regras.Any(regra => regra.DeveDescartar(transaction.Name)) ? null : transaction);
        });

        var tracerProvider = global::OpenTelemetry.Sdk.CreateTracerProviderBuilder()
            .AddSource(GiomActivitySource.Name)
            // Sem isso, a chamada HTTP de verdade pro provedor (Claude/OpenAI/Voyage/Cohere/
            // YouTube) acontecia DENTRO do Span da etapa (IniciarEtapaAtual) sem virar Span
            // filho — a etapa aparecia como caixa-preta no Sentry, sem visibilidade de
            // método/host/status/duração da chamada externa em si.
            .AddHttpClientInstrumentation(o =>
            {
                // Bug #128: redige segredo embutido na URL (ex.: token do bot do Telegram) ANTES
                // da Activity virar transação/span — SentryTransaction.Name é somente-leitura por
                // fora (ver SetBeforeSendTransaction acima), então a única forma confiável de
                // corrigir o nome que o Sentry acaba usando é já nascer redigido aqui, tanto no
                // DisplayName quanto nas tags de URL que a semântica OTel registra.
                if (todosScrubbers.Length == 0) return;

                o.EnrichWithHttpRequestMessage = (activity, request) =>
                {
                    var url = request.RequestUri?.ToString();
                    if (url is null) return;

                    var redigido = todosScrubbers.Aggregate(url, (atual, scrubber) => scrubber.Redigir(atual));
                    if (redigido == url) return;

                    activity.DisplayName = redigido;
                    activity.SetTag("url.full", redigido);
                    activity.SetTag("http.url", redigido);
                };
            })
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
