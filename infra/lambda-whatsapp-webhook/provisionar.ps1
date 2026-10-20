# Provisiona o webhook do WhatsApp (Task #140): fila SQS + DLQ, role IAM, Lambda e Function URL.
#
# Desenho (ver descricao da Task #140 pro porque): a Meta so entrega em HTTPS com certificado de
# CA e endereco estavel. O ECS do bot nao expoe porta e o IP da task Fargate muda a cada deploy;
# abrir isso pediria um ALB (~US$16/mes fixo) + dominio + certificado. A Function URL da Lambda
# ja da HTTPS, certificado valido e URL fixa de graca, e o container do bot continua sem porta
# aberta pra internet.
#
# Idempotente de ponta a ponta -- pode rodar quantas vezes quiser:
#   - create-queue converge atributos (mesmo padrao de infra/ecs-services-etapas/criar-filas-faltantes.ps1)
#   - create-role / put-role-policy sao upsert por nome
#   - a Lambda e criada na primeira vez e atualizada (codigo + env) nas seguintes
#   - create-function-url-config so roda se ainda nao existir
#
# Rodar do diretorio infra/lambda-whatsapp-webhook.
#
# Pre-requisito: os secrets ja precisam existir no SSM (o script LE de la, nunca grava):
#   /giom/{ambiente}/agenteinstituto/WhatsApp/VerifyToken   <- inventado por voce, repetido no painel da Meta
#   /giom/{ambiente}/agenteinstituto/WhatsApp/AppSecret     <- copiado do app no Meta Business

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("dev", "prod")]
    [string]$Ambiente
)

$ErrorActionPreference = "Stop"

$region      = "us-east-1"
$sufixo      = "-$Ambiente"
$nomeFila    = "giom-whatsapp-webhook$sufixo"
$nomeDlq     = "$nomeFila-dlq"
$nomeFuncao  = "giom-whatsapp-webhook$sufixo"
$nomeRole    = "giom-whatsapp-webhook-role$sufixo"
$ssmBase     = "/giom/$Ambiente/agenteinstituto/WhatsApp"

# Mesmos defaults de SqsOptions.cs / SqsFilaCanal.cs -- a fila precisa nascer igual ao que o
# SqsFilaCanal criaria sozinho, senao o create-queue do worker falha por atributo divergente.
$visibilityTimeout   = "300"
$maxReceiveCount     = "6"
$retencaoDlqSegundos = "1209600"

$tmpDir = Join-Path $env:TEMP "giom-whatsapp-webhook-$Ambiente"
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

# JSON sempre por arquivo (file://) e sem BOM: passar JSON inline pro aws.exe faz o PowerShell
# comer as aspas, e "Set-Content -Encoding utf8" no PS 5.1 grava BOM, que trava o parser do AWS
# CLI no primeiro caractere. Os dois bugs ja foram vividos neste repo (ver criar-filas-faltantes.ps1).
function Write-JsonFile {
    param([string]$Caminho, $Objeto)
    $json = $Objeto | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($Caminho, $json, (New-Object System.Text.UTF8Encoding $false))
    return $Caminho
}

function New-OrConvergeQueue {
    param([string]$Nome, [hashtable]$Atributos)

    $attrPath = Write-JsonFile (Join-Path $tmpDir "$Nome-attrs.json") $Atributos

    $existente = aws sqs get-queue-url --queue-name $Nome --region $region --query QueueUrl --output text 2>$null
    if ($LASTEXITCODE -eq 0 -and $existente) {
        Write-Host "Fila $Nome ja existe -- convergindo atributos"
        aws sqs set-queue-attributes --queue-url $existente --attributes "file://$attrPath" --region $region | Out-Null
        return $existente
    }

    Write-Host "Criando fila $Nome"
    return (aws sqs create-queue --queue-name $Nome --attributes "file://$attrPath" --region $region --query QueueUrl --output text)
}

Write-Host "== 1/5 Filas =="
$dlqUrl = New-OrConvergeQueue -Nome $nomeDlq -Atributos @{ MessageRetentionPeriod = $retencaoDlqSegundos }
$dlqArn = aws sqs get-queue-attributes --queue-url $dlqUrl --attribute-names QueueArn --region $region --query "Attributes.QueueArn" --output text

$redrivePolicy = @{ deadLetterTargetArn = $dlqArn; maxReceiveCount = $maxReceiveCount } | ConvertTo-Json -Compress
$filaUrl = New-OrConvergeQueue -Nome $nomeFila -Atributos @{
    VisibilityTimeout = $visibilityTimeout
    RedrivePolicy     = $redrivePolicy
}
$filaArn = aws sqs get-queue-attributes --queue-url $filaUrl --attribute-names QueueArn --region $region --query "Attributes.QueueArn" --output text

Write-Host "== 2/5 Role IAM =="
$trustPath = Write-JsonFile (Join-Path $tmpDir "trust.json") @{
    Version   = "2012-10-17"
    Statement = @(@{
        Effect    = "Allow"
        Principal = @{ Service = "lambda.amazonaws.com" }
        Action    = "sts:AssumeRole"
    })
}

$roleArn = aws iam get-role --role-name $nomeRole --query "Role.Arn" --output text 2>$null
if ($LASTEXITCODE -ne 0 -or -not $roleArn) {
    Write-Host "Criando role $nomeRole"
    $roleArn = aws iam create-role --role-name $nomeRole --assume-role-policy-document "file://$trustPath" --query "Role.Arn" --output text
    # Role recem-criada leva alguns segundos pra propagar antes de poder ser assumida pela Lambda.
    Start-Sleep -Seconds 10
} else {
    Write-Host "Role $nomeRole ja existe"
}

# Permissao minima: escrever NESTA fila e gravar log. Nada de banco, nada de chave de LLM --
# se a Lambda for comprometida, o pior caso e lixo entrando na fila.
$policyPath = Write-JsonFile (Join-Path $tmpDir "policy.json") @{
    Version   = "2012-10-17"
    Statement = @(
        @{ Effect = "Allow"; Action = @("sqs:SendMessage"); Resource = $filaArn },
        @{ Effect = "Allow"; Action = @("logs:CreateLogGroup", "logs:CreateLogStream", "logs:PutLogEvents"); Resource = "arn:aws:logs:*:*:*" }
    )
}
aws iam put-role-policy --role-name $nomeRole --policy-name "giom-whatsapp-webhook-policy" --policy-document "file://$policyPath" | Out-Null

Write-Host "== 3/5 Pacote =="
$zipPath = Join-Path $tmpDir "function.zip"
Remove-Item $zipPath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $PSScriptRoot "lambda_function.py") -DestinationPath $zipPath

Write-Host "== 4/5 Lambda =="
# Secrets vem do SSM na hora do provisionamento e viram variavel de ambiente da funcao (a AWS
# criptografa em repouso). Nunca ficam no repositorio -- ver CLAUDE.md.
$verifyToken = aws ssm get-parameter --name "$ssmBase/VerifyToken" --with-decryption --query "Parameter.Value" --output text --region $region
$appSecret   = aws ssm get-parameter --name "$ssmBase/AppSecret" --with-decryption --query "Parameter.Value" --output text --region $region

if ([string]::IsNullOrWhiteSpace($verifyToken) -or [string]::IsNullOrWhiteSpace($appSecret)) {
    throw "VerifyToken/AppSecret nao encontrados em $ssmBase -- cadastre no SSM antes de rodar."
}

$envPath = Write-JsonFile (Join-Path $tmpDir "env.json") @{
    Variables = @{ VERIFY_TOKEN = $verifyToken; APP_SECRET = $appSecret; QUEUE_URL = $filaUrl }
}

aws lambda get-function --function-name $nomeFuncao --region $region 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Lambda $nomeFuncao ja existe -- atualizando codigo e configuracao"
    aws lambda update-function-code --function-name $nomeFuncao --zip-file "fileb://$zipPath" --region $region | Out-Null
    aws lambda wait function-updated --function-name $nomeFuncao --region $region
    aws lambda update-function-configuration --function-name $nomeFuncao --environment "file://$envPath" --region $region | Out-Null
} else {
    Write-Host "Criando Lambda $nomeFuncao"
    aws lambda create-function --function-name $nomeFuncao --runtime python3.12 --role $roleArn --handler lambda_function.lambda_handler --zip-file "fileb://$zipPath" --timeout 10 --memory-size 128 --environment "file://$envPath" --region $region | Out-Null
}
aws lambda wait function-updated --function-name $nomeFuncao --region $region

Write-Host "== 5/5 Function URL =="
# AuthType NONE porque quem chama e a Meta, que nao assina requisicao com SigV4. O controle de
# acesso real e a assinatura X-Hub-Signature-256 conferida dentro da funcao (ver
# lambda_function.py + test_lambda_function.py).
$urlExistente = aws lambda get-function-url-config --function-name $nomeFuncao --region $region --query "FunctionUrl" --output text 2>$null
if ($LASTEXITCODE -ne 0 -or -not $urlExistente) {
    Write-Host "Criando Function URL"
    $urlExistente = aws lambda create-function-url-config --function-name $nomeFuncao --auth-type NONE --region $region --query "FunctionUrl" --output text

    # Sem esta permissao a Function URL responde 403 mesmo com auth NONE.
    aws lambda add-permission --function-name $nomeFuncao --statement-id "FunctionURLAllowPublicAccess" --action "lambda:InvokeFunctionUrl" --principal "*" --function-url-auth-type NONE --region $region | Out-Null
} else {
    Write-Host "Function URL ja existe"
}

Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Pronto ($Ambiente)."
Write-Host "  Fila:         $filaUrl"
Write-Host "  Function URL: $urlExistente"
Write-Host ""
Write-Host "Proximo passo (manual, no painel da Meta): cadastrar essa Function URL como webhook,"
Write-Host "com o mesmo VerifyToken do SSM, e assinar o campo 'messages'."
