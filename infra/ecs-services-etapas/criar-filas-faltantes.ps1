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

$tmpDir = Join-Path $env:TEMP "giom-filas-$Ambiente"
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

function New-OrConvergeQueue {
    param([string]$Nome, [hashtable]$Atributos)

    # JSON vai por arquivo (file://), nunca inline -- o PowerShell corrompe aspas duplas ao
    # passar string JSON como argumento pra um executavel nativo (aws.exe), fazendo o
    # --attributes chegar sem aspas nenhuma (bug real, achado ao vivo rodando este script).
    # Escrita via .NET direto (nao Set-Content -Encoding utf8) -- Windows PowerShell 5.1 grava
    # BOM com "utf8", e o parser JSON do AWS CLI (Python) trava no BOM logo no 1o caractere
    # (outro bug real, achado ao vivo na mesma rodada).
    $attrPath = Join-Path $tmpDir "$Nome-attrs.json"
    $attrJson = $Atributos | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($attrPath, $attrJson, (New-Object System.Text.UTF8Encoding $false))

    $existente = aws sqs get-queue-url --queue-name $Nome --region $region --query QueueUrl --output text 2>$null
    if ($LASTEXITCODE -eq 0 -and $existente) {
        Write-Host "Fila $Nome ja existe -- convergindo atributos (idempotente)"
        aws sqs set-queue-attributes --queue-url $existente --attributes "file://$attrPath" --region $region | Out-Null
        return $existente
    }

    Write-Host "Criando fila $Nome"
    $url = aws sqs create-queue --queue-name $Nome --attributes "file://$attrPath" --region $region --query QueueUrl --output text
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

Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue
Write-Host "Filas de $Ambiente conferidas/criadas."
