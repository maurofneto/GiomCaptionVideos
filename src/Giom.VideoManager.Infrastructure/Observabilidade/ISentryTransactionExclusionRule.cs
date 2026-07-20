namespace Giom.VideoManager.Infrastructure.Observabilidade;

/// <summary>
/// Regra que decide se uma transação de tracing deve ser descartada antes de sair pro Sentry
/// (Bug #128) — pra chamadas ruidosas e sem valor de observabilidade (ex.: long-polling do
/// Telegram, que dispara uma transação a cada ~30s pra sempre). Qualquer projeto Infrastructure.*
/// pode implementar e registrar a sua via DI, sem depender do SDK do Sentry: opera só sobre o
/// nome da transação (texto), agregado e aplicado genericamente por SentryBootstrap.
/// </summary>
public interface ISentryTransactionExclusionRule
{
    bool DeveDescartar(string nomeTransacao);
}
