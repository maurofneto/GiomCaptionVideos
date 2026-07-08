---
name: rodar-task-ecs
description: Dispara e monitora uma execução manual da task ECS Fargate do pipeline de vídeos deste projeto (Instituto ou Ministério, dev ou prod), e resume o resultado a partir dos logs do CloudWatch. Use sempre que o usuário pedir pra "rodar/forçar/disparar" o worker/processor em dev ou prod, "testar o deploy", "ver se resolveu" depois de alguma mudança de infra/config/secret, ou disser algo como "roda de novo", "bota pra rodar", "força a execução" — mesmo sem mencionar ECS ou run-task explicitamente.
---

# Rodar task ECS manualmente (Instituto / Ministério)

Os workers do pipeline (`Instituto.Worker`, `Ministerio.Worker`) rodam como ECS Task sob
demanda (`run-task`), não como Service de longa duração — não há agendamento automático ainda.
Toda validação de "isso resolveu?" depois de uma mudança passa por disparar a task na mão.

## Referência rápida

- Conta AWS `798229612288`, região `us-east-1`, cluster `giom-cluster`.
- Task definitions: `giom-instituto-dev-task`, `giom-instituto-task`, `giom-ministerio-dev-task`,
  `giom-ministerio-task` (sufixo `-dev` = ambiente dev; sem sufixo = prod).
- Log groups: `/ecs/giom-instituto-dev`, `/ecs/giom-instituto`, `/ecs/giom-ministerio-dev`,
  `/ecs/giom-ministerio` — stream `ecs/giom-{instituto,ministerio}/<taskId>`.

**Sempre prefixe comandos AWS CLI com argumento começando em `/` com `MSYS_NO_PATHCONV=1`**
(Git Bash no Windows reescreve `/ecs/...` como caminho local e quebra a chamada).

## Passos

1. **Confirme qual worker/ambiente** — se o usuário só disse "roda de novo" e o contexto da
   conversa já deixa claro qual (ex: acabou de mexer no Instituto dev), não precisa perguntar.
   Se ambíguo, pergunte.

2. **Pegue a revision mais recente da task definition**:
   ```bash
   MSYS_NO_PATHCONV=1 aws ecs describe-task-definition \
     --task-definition giom-instituto-dev-task \
     --query "taskDefinition.revision" --output text
   ```

3. **Pegue a config de rede** de um service/task conhecido (subnet + security group são
   estáveis, não mudam entre execuções — reaproveite em vez de perguntar):
   ```bash
   MSYS_NO_PATHCONV=1 aws ecs describe-services --cluster giom-cluster \
     --services giom-agenteinstituto-dev-service \
     --query "services[0].networkConfiguration" --output json
   ```

4. **Dispare a task**:
   ```bash
   MSYS_NO_PATHCONV=1 aws ecs run-task \
     --cluster giom-cluster \
     --task-definition giom-instituto-dev-task:<revision> \
     --launch-type FARGATE \
     --network-configuration "awsvpcConfiguration={subnets=[<subnet>],securityGroups=[<sg>],assignPublicIp=ENABLED}" \
     --query "tasks[0].taskArn" --output text
   ```
   Extraia o task ID (últimos 32 chars do ARN) — precisa dele pro passo seguinte.

5. **Monitore até parar.** Use a ferramenta `Monitor` com um loop `until...done` que faz
   `describe-tasks` a cada alguns segundos até `lastStatus` virar `STOPPED` — não fique
   chamando `sleep` + `describe-tasks` manualmente em turnos separados. Ao parar, confira
   `stoppedReason`/`exitCode` do container: `exitCode 0` = terminou limpo; qualquer outro
   valor (ex: `139`) indica crash — puxe os logs pra achar a causa real.

6. **Puxe só o que importa dos logs** — nunca despeje o log inteiro na resposta:
   ```bash
   MSYS_NO_PATHCONV=1 aws logs get-log-events \
     --log-group-name "/ecs/giom-instituto-dev" \
     --log-stream-name "ecs/giom-instituto/<taskId>" --output json
   ```
   Se a task terminou com erro, procure a primeira linha `Unhandled exception` ou `[ERR]` —
   é aí que está a causa raiz, o resto da stack trace geralmente é ruído de framework. Se
   terminou limpo, um resumo de 2-3 linhas confirmando o que processou já basta.

## Por que isso importa

`exitCode 0` sozinho não prova que o pipeline processou vídeo de verdade — já aconteceu de a
task "terminar limpo" sem ter feito nada útil. Sempre confira pelo menos uma linha de log
mostrando trabalho real de alguma etapa (`[EtapaQueueConsumer] Consumindo video.transcricao...`,
`[InstitutoPipelineHandler]`/`[MinisterioPipelineHandler] ... processado`) antes de reportar
sucesso ao usuário — os nomes de classe evoluem conforme a pipeline muda (ver
`docs/ARCHITECTURE.md` pro desenho atual), então confira o doc se o log não bater com o que
você espera.
