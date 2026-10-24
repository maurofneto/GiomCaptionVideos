# ECS Services + Application Auto Scaling (workers de etapa)

Substitui o padrão anterior (Lambda `giom-fila-watchman`/`-dev`, que reagia a poll de SQS
chamando `ecs.run_task` sob demanda — **decommissionada por completo em 2026-07-16**, ver seção
final) por **ECS Service de longa duração + Application Auto Scaling 0→N por profundidade de
fila SQS**, por etapa/ambiente. Motivo: os workers seguem modelo run-to-completion
(`SqsFilaConsumer` drena a fila e o processo termina sozinho), o que criava uma corrida
estrutural entre a Lambda e o ciclo de vida real das tasks — confirmado ao vivo em 2026-07-15
(3 mensagens redrivadas de uma DLQ ficaram presas 40+ minutos sem nenhum worker real pegá-las).

## Topologia — 1 Service por (etapa, ambiente)

| Etapa | Dev | Prod |
|---|---|---|
| Transcrição | `giom-transcricao-dev-service` → `giom-transcricao-dev-task` | `giom-transcricao-service` → `giom-transcricao-task` |
| Resumo | `giom-resumo-dev-service` → `giom-resumo-dev-task` | `giom-resumo-service` → `giom-resumo-task` |
| Categorização | `giom-categorizacao-dev-service` → `giom-categorizacao-dev-task` | `giom-categorizacao-service` → `giom-categorizacao-task` |
| Embedding | `giom-embedding-dev-service` → `giom-embedding-dev-task` | `giom-embedding-service` → `giom-embedding-task` |
| Orquestração | `giom-orquestracao-dev-service` → `giom-orquestracao-dev-task` | `giom-orquestracao-service` → `giom-orquestracao-task` |

**Nenhuma task definition nova** — reaproveita as que já existiam pra `run-task`.

### Por que não é 1 Service por canal

Em prod, hoje as filas de resumo/categorização/embedding existem só por canal
(`giom-videos-resumo-instituto-prod`/`-ministerio-prod` etc.) — resquício de antes da unificação
#104/#105 nunca finalizada. A task definition atual (`giom-resumo-task` etc.) não aponta pra
essas filas por canal, e sim pro nome unificado (`giom-videos-resumo-prod`), que não existia até
esta migração criar. **Decisão deliberada**: manter a topologia unificada que o código já
espera (1 Service por etapa), não criar Services por canal. As filas antigas por canal ficam
órfãs — reconciliar/limpar é trabalho separado, fora desta migração.

## Ordem de execução

1. `criar-filas-faltantes.ps1 -Ambiente prod` — cria as 4 filas+DLQ que faltam em prod
   (`giom-videos-{resumo,categorizacao,embedding,etapa-concluida}-prod`); em dev é no-op (já
   existem). Precisa rodar ANTES do resto — o alarme do autoscaling não observa fila inexistente.
2. `criar-service-linked-role.ps1` — 1x, conta inteira (não recebe `-Ambiente`).
3. `criar-services.ps1 -Ambiente dev|prod` — cria os 5 Services com `desiredCount=0` (nada
   dispara sozinho ainda).
4. `configurar-autoscaling.ps1 -Ambiente dev|prod` — scalable target + target-tracking + step
   scaling cold-start + CloudWatch alarm, por Service.

Todos os scripts são idempotentes — seguro rodar de novo se algo falhar no meio.

## Autoscaling — desenho

Target-tracking puro não escala de forma confiável a partir de 0 task (métrica SQS sem
proporção definida com 0 tasks rodando). Por isso cada Service tem duas policies:

- **Step scaling "cold start" 0→1**: atrelada a um CloudWatch Alarm simples
  (`ApproximateNumberOfMessagesVisible > 0`, período de 60s) — só precisa disparar uma vez pra
  tirar o Service de 0.
- **Target tracking** (regime normal): **Metric Math** combinando
  `ApproximateNumberOfMessagesVisible` (`AWS/SQS`) com `RunningTaskCount`
  (`ECS/ContainerInsights`) — `backlog_per_task = m1 / IF(m2==0,1,m2)`. Usar a métrica bruta da
  fila (sem dividir por task) inviabilizaria o Scale-In nativo da AWS: sem saber quantas tasks
  já estão rodando, o target-tracking não tem como perceber que "mesma fila, metade das
  mensagens, o dobro de tasks" já está sobre-provisionado. `TargetValue=5` (mensagens por
  task), `ScaleOutCooldown=60s`, `ScaleInCooldown=300s`.
  **Pré-requisito**: `RunningTaskCount` só existe como métrica de CloudWatch via **Container
  Insights** (não é uma métrica padrão do namespace `AWS/ECS`) — `configurar-autoscaling.ps1`
  garante isso habilitando Container Insights no cluster antes de configurar as policies
  (idempotente).

`maxCapacity`: 5 pra transcrição (I/O-bound — YouTube/Whisper), 3 pras demais (limitadas por
rate-limit dos provedores de IA).

## Scale-in sem `PutTaskProtection`

Deliberadamente não implementado. O modelo run-to-completion já trata mensagem-em-voo-perdida-
por-SIGTERM como retry natural (mensagem some da visibilidade, volta quando o
`VisibilityTimeout` expira — mesma proteção que já existe pra falha transitória, ver
`SqsFilaConsumer.cs`). `ScaleInCooldown=300s` já reduz a frequência desse evento a algo raro.
Revisitar só se o Sentry mostrar volume relevante de retries causados por scale-in.

## Grace period de idle no `SqsFilaConsumer`

O consumer (`src/Giom.VideoManager.Infrastructure.Sqs/SqsFilaConsumer.cs`) não morre mais no
primeiro long poll vazio — continua fazendo poll até `IdleGracePeriodSeconds` (default 180s/3min,
configurável por `SqsOptions`) sem mensagem nenhuma, só então encerra e deixa o processo
terminar. Reduz o churn de start/stop de task Fargate e encolhe a janela morta entre uma task
morrer e o autoscaling perceber fila não-vazia e subir outra.

## Decommissioning da Lambda watchman (concluído em 2026-07-16)

Validado em dev e em prod real (dev: pipeline completo com 5 vídeos reais do Instituto; prod:
teste controlado com 10 mensagens reais do Ministério/Instituto, incluindo um incidente real —
a ESM de `transcricao-prod` ainda ativa durante o primeiro teste causou 10 mensagens na DLQ por
correr contra o Service novo; corrigido desabilitando a ESM e redrivando a DLQ, confirmando o
Service novo sozinho processa tudo sem erro). Ordem executada:
1. Event Source Mappings deletadas (5 em dev, 1 em prod — a única que já existia antes desta
   migração).
2. Funções (`giom-fila-watchman`, `giom-fila-watchman-dev`) e a role
   `giom-lambda-watchman-role` deletadas.
3. `infra/lambda-fila-watchman/` removido do repo e o stage `Lambda` removido do
   pipeline (`azure-pipelines.yml`, hoje substituído por `.github/workflows/ci-cd.yml`).

Os EventBridge Schedules do modelo monolítico antigo (`giom-instituto-daily`,
`giom-ministerio-daily`) também foram desabilitados (não deletados) no mesmo momento, pra evitar
processamento duplicado enquanto o novo pipeline valida em prod.
