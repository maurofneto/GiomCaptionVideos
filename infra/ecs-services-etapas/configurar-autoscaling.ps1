# Configura Application Auto Scaling (0->N por profundidade de fila) pros 5 Services do
# ambiente dado. Padrao AWS pra SQS: target-tracking sozinho nao escala de forma confiavel a
# partir de 0 task -- por isso cada Service ganha TRES policies:
#   1. Step scaling "cold start" 0->1, atrelada a um CloudWatch Alarm simples
#      (ApproximateNumberOfMessagesVisible > 0) -- so precisa disparar uma vez.
#   2. Target tracking pra manter o regime dali em diante, via Metric Math combinando
#      AWS/SQS (ApproximateNumberOfMessagesVisible) com RunningTaskCount (mensagens POR TASK,
#      nao a contagem bruta da fila) -- e o que deixa o Scale-In nativo da AWS funcionar direito:
#      sem dividir pelo numero de tasks rodando, o target-tracking nao tem como saber que "a
#      mesma fila com metade das mensagens e o dobro de tasks" ja esta sobre-provisionada.
#   3. Step scaling "scale-in zero" 1->0, atrelada a um alarme de fila vazia.
#      Por que a 3a existe: target-tracking NAO leva a capacidade a zero -- ele mantem a
#      proporcao e simplesmente para em 1 quando a fila esvazia. Sem esta policy, o pipeline
#      inteiro fica com 1 task acordada por etapa, 24h por dia, mesmo sem nada pra processar.
#      Custo real medido em 19/08/2026: 11 tasks ociosas, com o IP publico de cada uma
#      (US$0,005/h) somando mais que o proprio processamento -- conta a caminho de US$80/mes
#      pra um pipeline que trata poucos videos por dia.
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
    } | ConvertTo-Json -Depth 8 | Set-Variable -Name ttConfigJson
    # Escrita via .NET direto, sem BOM -- Set-Content -Encoding utf8 no Windows PowerShell 5.1
    # grava BOM, e o parser JSON do AWS CLI (Python) trava no BOM logo no 1o caractere (bug
    # real, achado ao vivo rodando o script irmao criar-filas-faltantes.ps1).
    [System.IO.File]::WriteAllText($ttConfigPath, $ttConfigJson, (New-Object System.Text.UTF8Encoding $false))

    aws application-autoscaling put-scaling-policy `
        --service-namespace ecs `
        --scalable-dimension ecs:service:DesiredCount `
        --resource-id $resourceId `
        --policy-name "giom-$etapa$sufixo-backlog-tracking" `
        --policy-type TargetTrackingScaling `
        --target-tracking-scaling-policy-configuration "file://$ttConfigPath" `
        --region $region | Out-Null

    # --- Step scaling cold-start 0->1 ---
    # MetricIntervalLowerBound=0 (nao UpperBound=0) -- o alarme e GreaterThanThreshold, entao o
    # "breach amount" que a Application Auto Scaling recebe e POSITIVO (valor - limiar > 0).
    # UpperBound=0 cobre so valores <= 0 (nunca bate com um breach positivo) -- bug real, achado
    # ao vivo: a policy nunca tinha um degrau aplicavel e o scale-out simplesmente nao acontecia,
    # mesmo com o alarme em ALARM e ActionsEnabled=true (nenhuma scaling activity era registrada).
    $stepPolicyArn = aws application-autoscaling put-scaling-policy `
        --service-namespace ecs `
        --scalable-dimension ecs:service:DesiredCount `
        --resource-id $resourceId `
        --policy-name "giom-$etapa$sufixo-cold-start" `
        --policy-type StepScaling `
        --step-scaling-policy-configuration "AdjustmentType=ExactCapacity,StepAdjustments=[{MetricIntervalLowerBound=0,ScalingAdjustment=1}],Cooldown=60" `
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

    # --- Step scaling scale-in 1->0 ---
    # Espelho exato do cold-start acima, e o motivo do UpperBound aqui e o mesmo (invertido) do
    # LowerBound la: o alarme e LessThanThreshold, entao o breach amount chega NEGATIVO
    # (valor - limiar < 0), e so um degrau com MetricIntervalUpperBound=0 cobre isso.
    #
    # O limiar e 1 com LessThanThreshold, e nao 0 com LessThanOrEqual, de proposito: com fila
    # vazia o valor e 0, entao o breach vira 0-1 = -1, que cai com folga dentro de (-inf, 0].
    # Com limiar 0 o breach seria exatamente 0 -- em cima da fronteira do degrau, que e onde
    # esse tipo de policy silenciosamente nao aplica nenhum ajuste.
    $scaleInPolicyArn = aws application-autoscaling put-scaling-policy `
        --service-namespace ecs `
        --scalable-dimension ecs:service:DesiredCount `
        --resource-id $resourceId `
        --policy-name "giom-$etapa$sufixo-scale-in-zero" `
        --policy-type StepScaling `
        --step-scaling-policy-configuration "AdjustmentType=ExactCapacity,StepAdjustments=[{MetricIntervalUpperBound=0,ScalingAdjustment=0}],Cooldown=60" `
        --region $region `
        --query "PolicyARN" --output text

    # O alarme soma visiveis + EM VOO (NotVisible). Olhar so as visiveis derrubaria a task no
    # meio de um video: a mensagem sai da contagem de visiveis assim que e recebida, mas o
    # trabalho ainda esta acontecendo. Somando as duas, o Service so desce quando de fato nao
    # ha nada pendente nem em processamento.
    # 5 periodos de 60s evitam que uma janela curta entre dois videos zere o Service a toa.
    $scaleInMetricsPath = Join-Path $tmpDir "$etapa-scalein.json"
    @(
        @{
            Id         = "visiveis"
            MetricStat = @{
                Metric = @{
                    Namespace  = "AWS/SQS"
                    MetricName = "ApproximateNumberOfMessagesVisible"
                    Dimensions = @(@{ Name = "QueueName"; Value = $fila })
                }
                Period = 60
                Stat   = "Maximum"
            }
            ReturnData = $false
        },
        @{
            Id         = "emvoo"
            MetricStat = @{
                Metric = @{
                    Namespace  = "AWS/SQS"
                    MetricName = "ApproximateNumberOfMessagesNotVisible"
                    Dimensions = @(@{ Name = "QueueName"; Value = $fila })
                }
                Period = 60
                Stat   = "Maximum"
            }
            ReturnData = $false
        },
        @{
            Id         = "pendentes"
            Expression = "visiveis + emvoo"
            Label      = "MensagensPendentes"
            ReturnData = $true
        }
    ) | ConvertTo-Json -Depth 8 | Set-Variable -Name scaleInMetricsJson
    [System.IO.File]::WriteAllText($scaleInMetricsPath, $scaleInMetricsJson, (New-Object System.Text.UTF8Encoding $false))

    # notBreaching em missing data: o SQS para de publicar metrica de fila sem movimento nenhum,
    # e sem isso o alarme ficaria preso em INSUFFICIENT_DATA e nunca liberaria a descida.
    aws cloudwatch put-metric-alarm `
        --alarm-name "giom-$etapa$sufixo-fila-vazia" `
        --alarm-description "Fila vazia (nada visivel nem em voo) por 5 min -- libera o scale-in para 0." `
        --metrics "file://$scaleInMetricsPath" `
        --comparison-operator LessThanThreshold --threshold 1 --evaluation-periods 5 `
        --treat-missing-data notBreaching `
        --alarm-actions $scaleInPolicyArn `
        --region $region | Out-Null
}

Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue
Write-Host "Autoscaling de $Ambiente configurado (5 Services, target-tracking + cold-start 0->1 + scale-in 1->0)."
Write-Host "Com a fila vazia os Services descem a 0 em ~5min; a primeira mensagem faz subir em ~1min."
