"""
Webhook do WhatsApp (Task #140) — recebe o que a Meta empurra e joga na fila SQS.

Por que uma Lambda e nao um endpoint no proprio bot: a Meta so entrega em HTTPS com
certificado de CA e num endereco estavel. O ECS do bot nao expoe porta nenhuma, e o IP publico
da task Fargate muda a cada deploy — abrir isso exigiria um ALB (~US$16/mes fixo), mais dominio
e certificado. A Function URL da Lambda ja vem com HTTPS, certificado valido e URL fixa, de
graca, e mantem o container do bot sem porta aberta pra internet.

Esta funcao e burra de proposito: confere a assinatura e repassa o corpo CRU. Quem entende o
formato do payload e o WhatsAppWebhookProcessor, do lado C#. Assim, o dia que a Meta mudar o
formato, so o C# muda.

Responde 200 na hora, sem esperar processamento: a Meta reentrega tudo que demora demais, e
reentrega vira pergunta duplicada (e cobranca de LLM duplicada) la na frente.

Variaveis de ambiente: VERIFY_TOKEN, APP_SECRET, QUEUE_URL.
"""

import base64
import hashlib
import hmac
import json
import logging
import os

import boto3

logger = logging.getLogger()
logger.setLevel(logging.INFO)

sqs = boto3.client("sqs")

VERIFY_TOKEN = os.environ["VERIFY_TOKEN"]
APP_SECRET = os.environ["APP_SECRET"]
QUEUE_URL = os.environ["QUEUE_URL"]


def lambda_handler(event, context):
    metodo = event.get("requestContext", {}).get("http", {}).get("method", "").upper()

    if metodo == "GET":
        return _verificar(event)
    if metodo == "POST":
        return _receber(event)

    return _resposta(405, "metodo nao suportado")


def _verificar(event):
    """Desafio que a Meta faz uma unica vez, ao cadastrar a URL do webhook no painel."""
    params = event.get("queryStringParameters") or {}

    if params.get("hub.mode") == "subscribe" and params.get("hub.verify_token") == VERIFY_TOKEN:
        logger.info("Desafio de verificacao aceito")
        # A Meta espera o challenge cru no corpo, sem aspas e sem JSON em volta.
        return _resposta(200, params.get("hub.challenge", ""))

    logger.warning("Desafio de verificacao recusado — token nao confere")
    return _resposta(403, "token invalido")


def _receber(event):
    corpo_bytes = _corpo_em_bytes(event)

    if not _assinatura_confere(event, corpo_bytes):
        # Sem detalhe no log de proposito: nao ajuda quem esta sondando o endpoint.
        logger.warning("POST recusado — assinatura invalida")
        return _resposta(403, "assinatura invalida")

    # O corpo vai como string dentro de um envelope: o consumidor C# desserializa em
    # WhatsAppWebhookMensagem, cuja propriedade se chama "payload" (minusculo, declarado com
    # JsonPropertyName porque o System.Text.Json casa nome de forma case-sensitive).
    sqs.send_message(
        QueueUrl=QUEUE_URL,
        MessageBody=json.dumps({"payload": corpo_bytes.decode("utf-8")}),
    )

    # Sem logar o corpo: ele carrega o telefone do aluno (ver Task #143).
    logger.info("Webhook publicado na fila (%d bytes)", len(corpo_bytes))
    return _resposta(200, "ok")


def _corpo_em_bytes(event):
    corpo = event.get("body") or ""
    if event.get("isBase64Encoded"):
        return base64.b64decode(corpo)
    return corpo.encode("utf-8")


def _assinatura_confere(event, corpo_bytes):
    """
    HMAC-SHA256 do corpo CRU com o App Secret, exatamente como a Meta calculou. Precisa ser
    sobre os bytes originais — reserializar o JSON mudaria espacos/ordem e quebraria o hash.
    """
    # Headers chegam sempre em minusculo no formato de payload 2.0 da Function URL.
    recebida = (event.get("headers") or {}).get("x-hub-signature-256", "")
    if not recebida.startswith("sha256="):
        return False

    esperada = hmac.new(APP_SECRET.encode("utf-8"), corpo_bytes, hashlib.sha256).hexdigest()

    # compare_digest e nao "==": comparacao comum vaza, pelo tempo de resposta, quantos
    # caracteres iniciais bateram, e isso permite descobrir a assinatura por tentativa.
    return hmac.compare_digest(recebida[len("sha256="):], esperada)


def _resposta(status, corpo):
    return {
        "statusCode": status,
        "headers": {"Content-Type": "text/plain; charset=utf-8"},
        "body": corpo,
    }
