"""
Watchman da fila -> ECS RunTask, sem consumir a mensagem.

Substitui o par CloudWatch Alarm + EventBridge Rule (US #108-session, ver docs/ARCHITECTURE.md)
por um trigger quase em tempo real: o SQS Event Source Mapping da Lambda invoca a cada poll
(~1-20s) em vez de esperar a granularidade de 5 min do Alarm, e cada invocação tenta relançar a
task se ela falhar por um motivo transitório (ex: capacidade FARGATE_SPOT indisponível) --
o Alarm+Rule só disparava uma vez por transição OK->ALARM, então uma falha transitória deixava a
mensagem presa até intervenção manual (bug real encontrado em 2026-07-14, vídeo Nv-PCYxnp94).

Contrato com a Lambda: a mensagem NUNCA é apagada por aqui. Toda mensagem processada volta em
`batchItemFailures` -- quem consome de verdade é o SqsFilaConsumer do worker (retry/DLQ de
US #44/#45), não esta função. Isso preserva o mesmo princípio que corrigiu o bug dos EventBridge
Pipes: o mecanismo de trigger só observa a fila, nunca compete pela mensagem com o worker real.

CRÍTICO: o Event Source Mapping (SQS -> esta Lambda) PRECISA ser criado com
`--function-response-types ReportBatchItemFailures` (ver criar-event-source-mappings.ps1). Sem
essa flag, a AWS ignora completamente o dict `{"batchItemFailures": [...]}` que a função devolve
-- qualquer invocação que termine sem lançar exceção é tratada como sucesso TOTAL do batch, e a
AWS deleta a mensagem sozinha, mesmo que o código tenha explicitamente pedido "devolve pra fila,
não processei de verdade". Bug real achado ao vivo em 2026-07-15: 5 mensagens reais em
giom-videos-transcricao-prod sumiram sem nenhum worker (SqsFilaConsumer) jamais recebê-las e sem
ir pra DLQ -- a Lambda "processava" sem erro, a AWS deletava por conta própria, e o contrato
inteiro acima (mensagem nunca apagada aqui) ficava sem efeito nenhum na prática.

Deploy: uma função POR AMBIENTE -- giom-fila-watchman-dev (só filas -dev) e giom-fila-watchman
(prod, sem sufixo -- mesmo padrão assimétrico das task definitions ECS). Antes era uma função
única cobrindo os dois ambientes via Event Source Mapping nas filas -dev e -prod ao mesmo tempo,
mas isso misturava logs e triggers de dev/prod no mesmo CloudWatch Log Group, dificultando
diagnóstico (2026-07-15). O código continua o mesmo nas duas funções -- cada uma só tem Event
Source Mapping nas filas do próprio ambiente, então a resolução de família por env (ver
_disparar_task) nunca precisa lidar com o ambiente "errado" na prática.
"""

import os
import re

import boto3

ecs = boto3.client("ecs")
sqs = boto3.client("sqs")

CLUSTER_ARN = os.environ["CLUSTER_ARN"]
SUBNETS = os.environ["SUBNETS"].split(",")
SECURITY_GROUPS = os.environ["SECURITY_GROUPS"].split(",")

# giom-videos-{etapa}-{env}  |  giom-videos-etapa-concluida-{env} (etapa "orquestracao")
_PADRAO_FILA = re.compile(r"^giom-videos-(?P<etapa>.+)-(?P<env>dev|prod)$")


def _resolver_etapa_env(nome_fila: str) -> tuple[str, str] | None:
    m = _PADRAO_FILA.match(nome_fila)
    if not m:
        return None
    etapa = m.group("etapa")
    if etapa == "etapa-concluida":
        etapa = "orquestracao"
    return etapa, m.group("env")


def _task_ja_em_andamento(family: str) -> bool:
    # desiredStatus=RUNNING (default) inclui PROVISIONING/PENDING/RUNNING -- cobre a janela entre
    # o RunTask ter sido aceito e a task aparecer como RUNNING de verdade.
    resp = ecs.list_tasks(cluster=CLUSTER_ARN, family=family, maxResults=1)
    return len(resp.get("taskArns", [])) > 0


def _disparar_task(family: str, etapa: str) -> None:
    try:
        resp = ecs.run_task(
            cluster=CLUSTER_ARN,
            taskDefinition=family,
            capacityProviderStrategy=[{"capacityProvider": "FARGATE_SPOT", "weight": 1, "base": 0}],
            networkConfiguration={
                "awsvpcConfiguration": {
                    "subnets": SUBNETS,
                    "securityGroups": SECURITY_GROUPS,
                    "assignPublicIp": "ENABLED",
                }
            },
            startedBy=f"lambda-watchman/{etapa}",
        )
    except Exception as exc:
        # RunTask pode LANÇAR (ex: ClientException "TaskDefinition not found" -- bug real achado
        # em 2026-07-15, family com nome errado pro ambiente), não só devolver 200 com failures[]
        # preenchido (ex: capacidade indisponível). Se deixasse propagar, a invocação inteira da
        # Lambda falhava sem devolver batchItemFailures -- SQS trata isso como falha do BATCH
        # inteiro, e a mensagem só reaparece depois do VisibilityTimeout natural (300s) em vez do
        # reset explícito abaixo, queimando ApproximateReceiveCount sem o worker real nunca ter
        # recebido a mensagem (mesma classe de sintoma do bug #113). Por isso sempre capturamos
        # aqui -- a mensagem só é reprocessada no próximo poll (via reset de visibilidade abaixo,
        # que roda de qualquer forma), nunca derruba a invocação inteira.
        print(f"[watchman] RunTask lançou exceção pra {family}: {exc}")
        return

    for falha in resp.get("failures", []):
        # RunTask pode devolver 200 com failures[] preenchido (ex: capacidade indisponível) --
        # não é uma exceção, só logamos; a mensagem volta pra fila e o próximo poll tenta de novo.
        print(f"[watchman] RunTask falhou pra {family}: {falha.get('reason')}")


def handler(event, context):
    batch_item_failures = []

    for record in event.get("Records", []):
        message_id = record["messageId"]
        batch_item_failures.append({"itemIdentifier": message_id})

        queue_arn = record["eventSourceARN"]
        queue_name = queue_arn.rsplit(":", 1)[-1]
        resolvido = _resolver_etapa_env(queue_name)
        if not resolvido:
            print(f"[watchman] fila {queue_name} não bate com o padrão giom-videos-*-{{dev,prod}} -- ignorando")
            continue

        etapa, env = resolvido
        # Bug real achado em 2026-07-15: a convenção de nome de task definition NÃO é simétrica
        # entre ambientes -- dev tem sufixo "-dev" (giom-transcricao-dev-task), prod não tem
        # sufixo nenhum (giom-transcricao-task, sem "-prod"). Usar f"giom-{etapa}-{env}-task" pra
        # prod monta "giom-transcricao-prod-task", que nunca existiu -- RunTask lançava
        # ClientException "TaskDefinition not found" a cada mensagem de fila prod, o que por sua
        # vez (antes do fix acima) derrubava a invocação inteira sem processar nada de verdade.
        family = f"giom-{etapa}-{env}-task" if env == "dev" else f"giom-{etapa}-task"

        ja_em_andamento = _task_ja_em_andamento(family)
        if not ja_em_andamento:
            # Acabamos de chamar RunTask — a task nova leva ~15-40s pra provisionar (Fargate) até
            # começar seu próprio long-poll. Bug real achado ao vivo em 2026-07-15: resetar a
            # visibilidade pra 0 AGORA (incondicional, versão anterior deste código) faz a mensagem
            # ficar visível de novo antes da task nova sequer existir de verdade — a própria Lambda
            # (que invoca em ~100ms, muito mais rápido que a task provisiona) volta a receber a
            # MESMA mensagem em invocações seguintes e repete o ciclo, "vencendo a corrida" contra
            # o worker real e queimando o ApproximateReceiveCount sozinha (3 mensagens foram pra
            # DLQ sem nenhuma task real jamais ter processado, mesmo já com RunTask funcionando).
            # Por isso NÃO resetamos aqui — deixamos o VisibilityTimeout natural (300s, bem mais
            # que suficiente pra task subir e fazer seu próprio long-poll).
            _disparar_task(family, etapa)
        else:
            # Já tem uma task rodando (ou subindo) — ela já está fazendo (ou logo fará) seu
            # próprio long-poll na fila, então não tem corrida "Lambda vs. task nova" aqui. Resetar
            # pra 0 é seguro e útil: entrega a mensagem pra essa task já ativa imediatamente, sem
            # esperar o VisibilityTimeout inteiro (era esse o cenário original do bug #113 — task
            # rodando, mensagem presa até intervenção manual, vídeo RdlHhz56VuM).
            queue_url = sqs.get_queue_url(QueueName=queue_name)["QueueUrl"]
            sqs.change_message_visibility(
                QueueUrl=queue_url,
                ReceiptHandle=record["receiptHandle"],
                VisibilityTimeout=0,
            )

    return {"batchItemFailures": batch_item_failures}
