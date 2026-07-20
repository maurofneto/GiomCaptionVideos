namespace Giom.VideoManager.Infrastructure.Observabilidade;

/// <summary>
/// Implementação genérica de ISentryTransactionExclusionRule pra quando o critério é só "o nome
/// da transação contém este trecho" (Bug #128) — cobre o caso comum sem precisar de uma classe
/// própria por adapter.
/// </summary>
public sealed class SubstringSentryTransactionExclusionRule(string trecho) : ISentryTransactionExclusionRule
{
    public bool DeveDescartar(string nomeTransacao) =>
        nomeTransacao.Contains(trecho, StringComparison.OrdinalIgnoreCase);
}
