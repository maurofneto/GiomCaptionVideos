---
name: rodar-task-ecs
description: Dispara e monitora uma execução manual da task ECS Fargate do pipeline de vídeos deste projeto (Ingestão unificada, multi-canal; dev ou prod), e resume o resultado a partir dos logs do CloudWatch. Use sempre que o usuário pedir pra "rodar/forçar/disparar" o worker/processor em dev ou prod, "testar o deploy", "ver se resolveu" depois de alguma mudança de infra/config/secret, ou disser algo como "roda de novo", "bota pra rodar", "força a execução" — mesmo sem mencionar ECS ou run-task explicitamente.
---

# Rodar task ECS manualmente (Ingestão multi-canal)

A **Ingestão** (`Ingestao.Worker`, US #105) é a única ECS Task sob demanda (`run-task`): um worker
só pra todos os canais ativos (Instituto/Ministério), lidos de `canal_ingestao` no Supabase. Os
workers de etapa (transcrição, resumo, embedding, categorização, orquestração, agenteinstituto)
são ECS **Services** com autoscaling por fila SQS (0↔N) — não se disparam na mão: a Ingestão
publica na fila `giom-videos-transcricao-{env}` e a cadeia segue sozinha. Os antigos
`Instituto.Worker`/`Ministerio.Worker` não existem mais (os log groups `/ecs/giom-instituto*` e
`/ecs/giom-ministerio*` são legado).
Toda validação de "isso resolveu?" depois de uma mudança passa por disparar a Ingestão na mão.

## Referência rápida

- Conta AWS `798229612288`, região `us-east-1`, cluster `giom-cluster`.
- Task definitions: `giom-ingestao-dev-task` (dev) e `giom-ingestao-task` (prod).
- Log groups: `/ecs/giom-ingestao-dev` e `/ecs/giom-ingestao` — stream
  `ecs/giom-ingestao/<taskId>`. Etapas: `/ecs/giom-{transcricao,resumo,embedding,categorizacao,orquestracao,agenteinstituto}[-dev]`.
- Rede (subnet/SG): copie de `giom-agenteinstituto-dev-service` (ou `giom-agenteinstituto-service` em prod).

**Sempre prefixe comandos AWS CLI com argumento começando em `/` com `MSYS_NO_PATHCONV=1`**
(Git Bash no Windows reescreve `/ecs/...` como caminho local e quebra a chamada).

## Passos

1. **Confirme qual worker/ambiente** — se o usuário só disse "roda de novo" e o contexto da
   conversa já deixa claro qual (ex: acabou de mexer no Instituto dev), não precisa perguntar.
   Se ambíguo, pergunte.

2. **Pegue a revision mais recente da task definition**:
   ```bash
   MSYS_NO_PATHCONV=1 aws ecs describe-task-definition \
     --task-definition giom-ingestao-dev-task \
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
     --task-definition giom-ingestao-dev-task:<revision> \
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
     --log-group-name "/ecs/giom-ingestao-dev" \
     --log-stream-name "ecs/giom-ingestao/<taskId>" --output json
   ```
   Se a task terminou com erro, procure a primeira linha `Unhandled exception` ou `[ERR]` —
   é aí que está a causa raiz, o resto da stack trace geralmente é ruído de framework. Se
   terminou limpo, um resumo de 2-3 linhas confirmando o que processou já basta.

## Armadilha: exit 0 sem trabalho

Se o refresh token OAuth do Google de um canal expirar (`invalid_grant`), o canal é pulado, o
`ChannelProcessor` ainda imprime "Processamento concluído" e a task sai com **exit 0** sem processar
nada (Bug #147: agora gera erro/Critical no Sentry, mas o exit code continua 0). Procure `[ERR]` em
`AddIngestaoMultiCanal` antes de declarar sucesso. Depois de rodar, confirme também que a fila
`giom-videos-transcricao-{env}` recebeu mensagens e que o service de transcrição subiu.

## Por que isso importa

`exitCode 0` sozinho não prova que o pipeline processou vídeo de verdade — já aconteceu de a
task "terminar limpo" sem ter feito nada útil. Sempre confira pelo menos uma linha de log
mostrando trabalho real de alguma etapa (`[EtapaQueueConsumer] Consumindo video.transcricao...`,
`[ChannelProcessor]` com vídeos processados por canal) antes de reportar
sucesso ao usuário — os nomes de classe evoluem conforme a pipeline muda (ver
`docs/ARCHITECTURE.md` pro desenho atual), então confira o doc se o log não bater com o que
você espera.
