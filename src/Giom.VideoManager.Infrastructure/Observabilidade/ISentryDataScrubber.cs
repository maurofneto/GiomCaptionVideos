namespace Giom.VideoManager.Infrastructure.Observabilidade;

/// <summary>
/// Redige dado sensível (token, api key, etc.) que apareça em texto de telemetria antes de sair
/// pro Sentry (Bug #128) — a instrumentação automática de HttpClient nomeia transações/spans com
/// a URL inteira, e provedores como o Telegram embutem credencial direto na URL (ex.:
/// api.telegram.org/bot&lt;TOKEN&gt;/metodo). Cada projeto Infrastructure.* que sabe onde seu
/// próprio segredo pode vazar contribui sua própria implementação via DI; SentryBootstrap aplica
/// todas, em cadeia, sobre toda transação antes de enviar.
/// </summary>
public interface ISentryDataScrubber
{
    string Redigir(string texto);
}
