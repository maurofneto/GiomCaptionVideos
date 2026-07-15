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
        family = f"giom-{etapa}-{env}-task"

        ja_em_andamento = _task_ja_em_andamento(family)
        if not ja_em_andamento:
            _disparar_task(family, etapa)

        # Bug #113: o ReceiveMessage que ESSA função acabou de fazer (inerente ao Event Source
        # Mapping, acontece mesmo quando decidimos não lançar task) incrementa o MESMO
        # ApproximateReceiveCount que o worker real usa pra contar tentativa -- se a mensagem
        # ficasse escondida até o VisibilityTimeout padrão (300s) toda vez que achássemos "task já
        # rodando", uma sequência de invocações da Lambda podia "gastar" o maxReceiveCount inteiro
        # sem o worker real NUNCA ter recebido a mensagem (visto ao vivo em 2026-07-14, vídeo
        # RdlHhz56VuM foi pra DLQ sem nenhuma das >5 tasks reais jamais ter processado). Por isso
        # sempre resetamos a 0 aqui, incondicionalmente -- o custo é mais invocações da Lambda
        # competindo com o worker pela mesma mensagem, que é barato; SqsOptions.MaxReceiveCount
        # subiu de 3 pra 6 (ver comentário lá) pra dar folga extra a essas inspeções.
        queue_url = sqs.get_queue_url(QueueName=queue_name)["QueueUrl"]
        sqs.change_message_visibility(
            QueueUrl=queue_url,
            ReceiptHandle=record["receiptHandle"],
            VisibilityTimeout=0,
        )

    return {"batchItemFailures": batch_item_failures}
