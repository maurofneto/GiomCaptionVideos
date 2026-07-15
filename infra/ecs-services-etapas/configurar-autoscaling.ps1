# Configura Application Auto Scaling (0->N por profundidade de fila) pros 5 Services do
# ambiente dado. Padrao AWS pra SQS: target-tracking sozinho nao escala de forma confiavel a
# partir de 0 task -- por isso cada Service ganha DUAS policies:
#   1. Step scaling "cold start" 0->1, atrelada a um CloudWatch Alarm simples
#      (ApproximateNumberOfMessagesVisible > 0) -- so precisa disparar uma vez.
#   2. Target tracking pra manter o regime dali em diante, via Metric Math combinando
#      AWS/SQS (ApproximateNumberOfMessagesVisible) com RunningTaskCount (mensagens POR TASK,
#      nao a contagem bruta da fila) -- e o que deixa o Scale-In nativo da AWS funcionar direito:
#      sem dividir pelo numero de tasks rodando, o target-tracking nao tem como saber que "a
#      mesma fila com metade das mensagens e o dobro de tasks" ja esta sobre-provisionada.
# Nota: RunningTaskCount so existe como metrica de CloudWatch via Container Insights (namespace
# ECS/ContainerInsights, dimensoes ClusterName+ServiceName) -- nao faz parte do AWS/ECS padrao.
# Por isso o script garante Container Insights habilitado no cluster antes de configurar a
# policy (idempotente -- update-cluster-settings e upsert).
#
# Idempotente: register-scalable-target, put-scaling-policy e put-metric-alarm sao upsert por
# design da AWS -- reexecutar com os mesmos parametros nao duplica nem falha.
# Rodar do diretorio infra/ecs-services-etapas, DEPOIS de criar-services.ps1.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("dev", "prod")]
    [string]$Ambiente
)

$cluster = "giom-cluster"
$region = "us-east-1"
$sufixo = if ($Ambiente -eq "dev") { "-dev" } else { "" }
$sufixoFila = if ($Ambiente -eq "dev") { "-dev" } else { "-prod" }

# transcricao/resumo/categorizacao/embedding: maxCapacity=3 (limitadas por rate-limit dos
# provedores de IA); transcricao e I/O-bound (YouTube/Whisper), maxCapacity=5.
$maxCapacity = @{
    transcricao    = 5
    resumo         = 3
    categorizacao  = 3
    embedding      = 3
    orquestracao   = 3
}

# orquestracao consome a fila etapa-concluida, nao "giom-videos-orquestracao-*"
$filaPorEtapa = @{
    transcricao   = "giom-videos-transcricao$sufixoFila"
    resumo        = "giom-videos-resumo$sufixoFila"
    categorizacao = "giom-videos-categorizacao$sufixoFila"
    embedding     = "giom-videos-embedding$sufixoFila"
    orquestracao  = "giom-videos-etapa-concluida$sufixoFila"
}

# Container Insights e pre-requisito pra RunningTaskCount existir como metrica de CloudWatch --
# idempotente (update-cluster-settings converge pro valor pedido, nao falha se ja estiver assim).
aws ecs update-cluster-settings `
    --cluster $cluster `
    --settings name=containerInsights,value=enabled `
    --region $region | Out-Null

$tmpDir = Join-Path $env:TEMP "giom-autoscaling-$Ambiente"
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

foreach ($etapa in @("transcricao", "resumo", "categorizacao", "embedding", "orquestracao")) {
    $servico = "giom-$etapa$sufixo-service"
    $resourceId = "service/$cluster/$servico"
    $fila = $filaPorEtapa[$etapa]
    $max = $maxCapacity[$etapa]

    Write-Host "=== $servico (fila $fila, maxCapacity=$max) ==="

    aws application-autoscaling register-scalable-target `
        --service-namespace ecs `
        --scalable-dimension ecs:service:DesiredCount `
        --resource-id $resourceId `
        --min-capacity 0 --max-capacity $max `
        --region $region | Out-Null

    # --- Target tracking (regime normal) — Metric Math: mensagens visiveis / task rodando ---
    # m1 = ApproximateNumberOfMessagesVisible (AWS/SQS), m2 = RunningTaskCount
    # (ECS/ContainerInsights, por Cluster+Service). backlog_per_task usa IF(m2==0,1,m2) pra
    # nunca dividir por zero -- na pratica m2=0 so acontece na janela que o step-scaling
    # cold-start (0->1) ja cobre, target-tracking so entra em regime com >=1 task rodando.
    $ttConfigPath = Join-Path $tmpDir "$etapa-tt.json"
    @{
        TargetValue = 5.0
        CustomizedMetricSpecification = @{
            Metrics = @(
                @{
                    Id         = "m1"
                    MetricStat = @{
                        Metric = @{
                            MetricName = "ApproximateNumberOfMessagesVisible"
                            Namespace  = "AWS/SQS"
                            Dimensions = @(@{ Name = "QueueName"; Value = $fila })
                        }
                        Stat = "Maximum"
                    }
                    ReturnData = $false
                },
                @{
                    Id         = "m2"
                    MetricStat = @{
                        Metric = @{
                            MetricName = "RunningTaskCount"
                            Namespace  = "ECS/ContainerInsights"
                            Dimensions = @(
                                @{ Name = "ClusterName"; Value = $cluster }
                                @{ Name = "ServiceName"; Value = $servico }
                            )
                        }
                        Stat = "Average"
                    }
                    ReturnData = $false
                },
                @{
                    Id         = "backlog_per_task"
                    Expression = "m1 / IF(m2==0,1,m2)"
                    Label      = "MensagensPorTask"
                    ReturnData = $true
                }
            )
        }
        ScaleOutCooldown = 60
        ScaleInCooldown  = 300
    } | ConvertTo-Json -Depth 8 | Set-Content -Path $ttConfigPath -Encoding utf8

    aws application-autoscaling put-scaling-policy `
        --service-namespace ecs `
        --scalable-dimension ecs:service:DesiredCount `
        --resource-id $resourceId `
        --policy-name "giom-$etapa$sufixo-backlog-tracking" `
        --policy-type TargetTrackingScaling `
        --target-tracking-scaling-policy-configuration "file://$ttConfigPath" `
        --region $region | Out-Null

    # --- Step scaling cold-start 0->1 ---
    $stepPolicyArn = aws application-autoscaling put-scaling-policy `
        --service-namespace ecs `
        --scalable-dimension ecs:service:DesiredCount `
        --resource-id $resourceId `
        --policy-name "giom-$etapa$sufixo-cold-start" `
        --policy-type StepScaling `
        --step-scaling-policy-configuration "AdjustmentType=ExactCapacity,StepAdjustments=[{MetricIntervalUpperBound=0,ScalingAdjustment=1}],Cooldown=60" `
        --region $region `
        --query "PolicyARN" --output text

    aws cloudwatch put-metric-alarm `
        --alarm-name "giom-$etapa$sufixo-fila-nao-vazia" `
        --namespace "AWS/SQS" `
        --metric-name "ApproximateNumberOfMessagesVisible" `
        --dimensions "Name=QueueName,Value=$fila" `
        --statistic Maximum --period 60 --evaluation-periods 1 `
        --threshold 0 --comparison-operator GreaterThanThreshold `
        --alarm-actions $stepPolicyArn `
        --region $region | Out-Null
}

Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue
Write-Host "Autoscaling de $Ambiente configurado (5 Services, target-tracking + cold-start 0->1)."
