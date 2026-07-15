# Cria os Event Source Mappings (SQS -> Lambda) do watchman, um por fila, ja com
# --function-response-types ReportBatchItemFailures.
#
# CRITICO: sem essa flag, a AWS ignora o retorno {"batchItemFailures": [...]} da funcao e
# DELETA a mensagem sozinha sempre que a invocacao termina sem lancar excecao -- mesmo que o
# codigo explicitamente devolva a mensagem na lista de "falhas" pra dizer "nao processei de
# verdade, devolve pra fila". Bug real encontrado ao vivo em 2026-07-15: 5 mensagens reais
# sumiram da fila giom-videos-transcricao-prod sem nenhum worker real (SqsFilaConsumer) jamais
# as receber, sem ir pra DLQ -- a Lambda "processava" (sem excecao) e a AWS deletava por conta
# propria, ignorando por completo a intencao de "nunca apagar aqui" documentada no modulo.
# Rodar do diretorio infra/lambda-fila-watchman.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("dev", "prod")]
    [string]$Ambiente
)

$funcao = if ($Ambiente -eq "dev") { "giom-fila-watchman-dev" } else { "giom-fila-watchman" }
$sufixo = if ($Ambiente -eq "dev") { "-dev" } else { "-prod" }

# giom-videos-etapa-concluida-{env} tambem faz parte do conjunto (etapa "orquestracao" na Lambda).
$etapas = @("transcricao", "resumo", "categorizacao", "embedding", "etapa-concluida")

foreach ($etapa in $etapas) {
    $arn = "arn:aws:sqs:us-east-1:798229612288:giom-videos-$etapa$sufixo"
    aws lambda create-event-source-mapping `
        --function-name $funcao `
        --event-source-arn $arn `
        --batch-size 1 `
        --function-response-types ReportBatchItemFailures `
        --region us-east-1
}
