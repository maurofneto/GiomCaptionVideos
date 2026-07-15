# Cria os 5 ECS Services de longa duracao (1 por etapa) pro ambiente dado, reaproveitando as
# task definitions ja existentes (nenhuma task definition nova) -- ver
# infra/ecs-services-etapas/README.md pra topologia completa e por que e 1 por etapa, nao por
# canal. desiredCount comeca em 0 -- nada dispara sozinho ate configurar-autoscaling.ps1 rodar.
#
# Idempotente: describe-services primeiro; se o Service ja existe e esta ACTIVE, so roda
# update-service (idempotente por natureza) se algum parametro relevante mudou (aqui, so
# garante que segue com a task definition/rede certas); senao no-op. So chama create-service se
# nao existir ou estiver INACTIVE.
# Rodar do diretorio infra/ecs-services-etapas.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("dev", "prod")]
    [string]$Ambiente
)

$cluster = "giom-cluster"
$region = "us-east-1"
$sufixo = if ($Ambiente -eq "dev") { "-dev" } else { "" }

# Mesma subnet/SG do giom-agenteinstituto-service -- unico precedente real de Service de longa
# duracao ja validado em prod (ver achado do plano de migracao, 2026-07).
$subnet = "subnet-00d0f65b5d9be1b84"
$securityGroup = "sg-01824f63de2fde212"

$etapas = @("transcricao", "resumo", "categorizacao", "embedding", "orquestracao")

foreach ($etapa in $etapas) {
    $servico = "giom-$etapa$sufixo-service"
    $taskDef = "giom-$etapa$sufixo-task"

    $existente = aws ecs describe-services --cluster $cluster --services $servico --region $region --query "services[?status=='ACTIVE'] | [0].serviceName" --output text 2>$null

    if ($existente -and $existente -ne "None") {
        Write-Host "Service $servico ja existe e esta ACTIVE -- convergindo task definition/rede (idempotente)"
        aws ecs update-service `
            --cluster $cluster `
            --service $servico `
            --task-definition $taskDef `
            --network-configuration "awsvpcConfiguration={subnets=[$subnet],securityGroups=[$securityGroup],assignPublicIp=ENABLED}" `
            --region $region | Out-Null
        continue
    }

    Write-Host "Criando Service $servico (task definition $taskDef, desiredCount=0)"
    # --capacity-provider-strategy e --launch-type sao mutuamente exclusivos no create-service --
    # so o primeiro (mesma estrategia FARGATE_SPOT que a Lambda watchman usava no run-task).
    aws ecs create-service `
        --cluster $cluster `
        --service-name $servico `
        --task-definition $taskDef `
        --desired-count 0 `
        --capacity-provider-strategy "capacityProvider=FARGATE_SPOT,weight=1,base=0" `
        --network-configuration "awsvpcConfiguration={subnets=[$subnet],securityGroups=[$securityGroup],assignPublicIp=ENABLED}" `
        --region $region | Out-Null
}

Write-Host "Services de $Ambiente conferidos/criados (desiredCount=0 -- configure o autoscaling antes de esperar processamento real)."
