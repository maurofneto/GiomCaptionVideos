# ECS Services + Application Auto Scaling (workers de etapa)

Substitui o padrão anterior (`infra/lambda-fila-watchman/`) — Lambda watchman que reagia a
poll de SQS chamando `ecs.run_task` sob demanda — por **ECS Service de longa duração + Application
Auto Scaling 0→N por profundidade de fila SQS**, por etapa/ambiente. Motivo: os workers seguem
modelo run-to-completion (`SqsFilaConsumer` drena a fila e o processo termina sozinho), o que
criava uma corrida estrutural entre a Lambda e o ciclo de vida real das tasks — confirmado ao
vivo em 2026-07-15 (3 mensagens redrivadas de uma DLQ ficaram presas 40+ minutos sem nenhum
worker real pegá-las).

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
- **Target tracking** (regime normal): métrica `ApproximateNumberOfMessagesVisible` bruta
  (não "mensagens por task" via metric math) — simplificação deliberada pra evitar depender de
  Container Insights (que forneceria `RunningTaskCount`, indisponível se não habilitado no
  cluster) e manter a config mais fácil de depurar. `TargetValue=5`, `ScaleOutCooldown=60s`,
  `ScaleInCooldown=300s`. Se o comportamento de scale-out/scale-in não convergir bem na prática
  (oscilação, sub/sobre-provisionamento), revisitar pra metric math por-task.

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

## Decommissioning da Lambda watchman

Só depois de validar os 10 Services em prod real (ciclo completo de scale-out/scale-in
observado). Ordem seguindo `../lambda-fila-watchman/`:
1. Deletar os Event Source Mappings (`aws lambda delete-event-source-mapping`) — para os
   triggers sem apagar mais nada.
2. Janela de observação (mín. 24h) confirmando que os Services processam tudo sozinhos.
3. Deletar as funções (`giom-fila-watchman`, `giom-fila-watchman-dev`) e a role
   `giom-lambda-watchman-role`.
4. Remover `infra/lambda-fila-watchman/` do repo e o stage `Lambda` do `azure-pipelines.yml`.

Este é um passo destrutivo em infra real — pedir confirmação explícita separada antes de
executar, não fazer automaticamente.
