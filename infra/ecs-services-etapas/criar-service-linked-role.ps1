# Cria a service-linked role que o Application Auto Scaling usa pra gerenciar ECS Services
# (AWSServiceRoleForApplicationAutoScaling_ECSService) -- pre-requisito de conta, 1x so, antes
# de qualquer register-scalable-target. Idempotente: se ja existir, a AWS devolve
# InvalidInput/"has been taken", que capturamos e ignoramos.
# Rodar do diretorio infra/ecs-services-etapas (nao recebe parametro -- e por conta, nao por ambiente).

$erro = aws iam create-service-linked-role --aws-service-name ecs.application-autoscaling.amazonaws.com 2>&1
if ($LASTEXITCODE -ne 0) {
    if ($erro -match "has been taken|already exists") {
        Write-Host "Service-linked role ja existe -- ok (idempotente)."
    } else {
        Write-Error "Falha inesperada ao criar a service-linked role: $erro"
        exit 1
    }
} else {
    Write-Host "Service-linked role criada."
}
