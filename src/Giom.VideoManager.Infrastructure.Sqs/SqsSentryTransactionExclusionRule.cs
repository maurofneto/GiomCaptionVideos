using Giom.VideoManager.Infrastructure.Observabilidade;

namespace Giom.VideoManager.Infrastructure.Sqs;

/// <summary>
/// Descarta as transações da própria chamada HTTP do AWS SDK pro SQS (Bug #133 — mesma causa
/// raiz do polling do Telegram, Bug #128) — ReceiveMessage roda em long-poll contínuo por fila
/// (5 filas: transcrição/resumo/categorização/embedding/etapa-concluída), e sozinho já é o
/// maior gerador de spans do projeto (1676 numa janela de 24h, mais que a soma de todas as
/// transações reais de pipeline). Sem segredo na URL aqui (SQS autentica via SigV4 em header,
/// não na URL como o Telegram) — só ruído puro, cabe exclusão sem scrubber. Tipo nomeado (não
/// SubstringSentryTransactionExclusionRule inline) de propósito: AddSqsFilaEtapa é chamado uma
/// vez por fila, e TryAddEnumerable só dedupa registros por tipo de implementação — um registro
/// só, não 5 repetidos.
/// </summary>
public sealed class SqsSentryTransactionExclusionRule : ISentryTransactionExclusionRule
{
    public bool DeveDescartar(string nomeTransacao) =>
        nomeTransacao.Contains("sqs.", StringComparison.OrdinalIgnoreCase);
}
