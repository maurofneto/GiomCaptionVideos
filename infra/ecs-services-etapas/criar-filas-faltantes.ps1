# Cria as filas SQS (+ DLQ) que ainda nao existem em prod, com os MESMOS atributos que o
# SqsFilaCanal (src/Giom.VideoManager.Infrastructure.Sqs/SqsFilaCanal.cs) usaria ao auto-criar
# na primeira mensagem -- precisa existir ANTES do Service subir pra 1 vez, senao o alarme de
# CloudWatch do autoscaling nao tem metrica pra observar (fila inexistente = sem dado = alarme
# nunca dispara, scale 0->1 nunca acontece).
#
# Idempotente: create-queue com os MESMOS atributos e um no-op se a fila ja existe (a AWS so
# falha com QueueNameExists se os atributos divergirem -- por isso capturamos e convergimos via
# set-queue-attributes, espelhando CriarOuConvergirAsync do SqsFilaCanal). Seguro rodar em dev
# tambem (as filas dev ja existem com esses atributos, vira no-op).
# Rodar do diretorio infra/ecs-services-etapas.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("dev", "prod")]
    [string]$Ambiente
)

$sufixo = if ($Ambiente -eq "dev") { "-dev" } else { "-prod" }
$region = "us-east-1"

# maxReceiveCount=6 / VisibilityTimeout=300 / RetencaoDlq=14 dias -- mesmos valores default de
# SqsOptions.cs (MaxReceiveCount, VisibilityTimeout) e SqsFilaCanal.cs (RetencaoDlqSegundos).
$visibilityTimeout = "300"
$maxReceiveCount = "6"
$retencaoDlqSegundos = "1209600"

# So as etapas que hoje faltam fila unificada em prod (transcricao-prod ja existe; dev ja tem
# as 5). Rodar mesmo assim pras 5 -- e idempotente, e cobre o caso de uma fila dev ter sumido.
$etapas = @("transcricao", "resumo", "categorizacao", "embedding", "etapa-concluida")

function New-OrConvergeQueue {
    param([string]$Nome, [hashtable]$Atributos)

    $existente = aws sqs get-queue-url --queue-name $Nome --region $region --query QueueUrl --output text 2>$null
    if ($LASTEXITCODE -eq 0 -and $existente) {
        Write-Host "Fila $Nome ja existe -- convergindo atributos (idempotente)"
        $attrJson = $Atributos | ConvertTo-Json -Compress
        aws sqs set-queue-attributes --queue-url $existente --attributes $attrJson --region $region | Out-Null
        return $existente
    }

    Write-Host "Criando fila $Nome"
    $attrJson = $Atributos | ConvertTo-Json -Compress
    $url = aws sqs create-queue --queue-name $Nome --attributes $attrJson --region $region --query QueueUrl --output text
    return $url
}

foreach ($etapa in $etapas) {
    $nomeFila = "giom-videos-$etapa$sufixo"
    $nomeDlq = "$nomeFila-dlq"

    $dlqUrl = New-OrConvergeQueue -Nome $nomeDlq -Atributos @{ MessageRetentionPeriod = $retencaoDlqSegundos }
    $dlqArn = aws sqs get-queue-attributes --queue-url $dlqUrl --attribute-names QueueArn --region $region --query "Attributes.QueueArn" --output text

    $redrivePolicy = @{ deadLetterTargetArn = $dlqArn; maxReceiveCount = $maxReceiveCount } | ConvertTo-Json -Compress
    New-OrConvergeQueue -Nome $nomeFila -Atributos @{
        VisibilityTimeout = $visibilityTimeout
        RedrivePolicy     = $redrivePolicy
    } | Out-Null
}

Write-Host "Filas de $Ambiente conferidas/criadas."
