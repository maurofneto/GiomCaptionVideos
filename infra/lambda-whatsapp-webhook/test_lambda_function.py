"""
Testes da Lambda do webhook (Task #140). Rodar: python -m unittest -v (nesta pasta).

Cobre principalmente a validacao de assinatura: e o unico controle de acesso do endpoint, que
fica publico na internet. Um bug ali deixa qualquer um injetar mensagem na fila do bot.

boto3 e stubado — o teste nao toca AWS.
"""

import base64
import hashlib
import hmac
import json
import sys
import unittest
from types import SimpleNamespace, ModuleType
from unittest.mock import MagicMock

APP_SECRET = "segredo-de-teste"
VERIFY_TOKEN = "token-de-verificacao"
QUEUE_URL = "https://sqs.us-east-1.amazonaws.com/000000000000/fila-de-teste"

# Stub do boto3 antes do import do modulo, que cria o cliente no nivel do modulo.
_sqs_fake = MagicMock()
_boto3 = ModuleType("boto3")
_boto3.client = lambda *_args, **_kwargs: _sqs_fake
sys.modules["boto3"] = _boto3

import os
os.environ.update(APP_SECRET=APP_SECRET, VERIFY_TOKEN=VERIFY_TOKEN, QUEUE_URL=QUEUE_URL)

import lambda_function  # noqa: E402


def evento(metodo, body=None, headers=None, query=None, base64_encoded=False):
    return {
        "requestContext": {"http": {"method": metodo}},
        "headers": headers or {},
        "queryStringParameters": query,
        "body": body,
        "isBase64Encoded": base64_encoded,
    }


def assinar(corpo_bytes, segredo=APP_SECRET):
    return "sha256=" + hmac.new(segredo.encode(), corpo_bytes, hashlib.sha256).hexdigest()


class DesafioDeVerificacao(unittest.TestCase):
    def test_token_correto_devolve_o_challenge_cru(self):
        resposta = lambda_function.lambda_handler(evento("GET", query={
            "hub.mode": "subscribe", "hub.verify_token": VERIFY_TOKEN, "hub.challenge": "1158201444",
        }), None)

        self.assertEqual(200, resposta["statusCode"])
        # Precisa ser o valor puro: a Meta compara o corpo inteiro com o challenge que mandou.
        self.assertEqual("1158201444", resposta["body"])

    def test_token_errado_e_recusado(self):
        resposta = lambda_function.lambda_handler(evento("GET", query={
            "hub.mode": "subscribe", "hub.verify_token": "chute", "hub.challenge": "123",
        }), None)

        self.assertEqual(403, resposta["statusCode"])


class RecebimentoDeMensagem(unittest.TestCase):
    def setUp(self):
        _sqs_fake.reset_mock()

    def test_assinatura_valida_publica_o_corpo_cru_na_fila(self):
        corpo = json.dumps({"entry": [{"changes": []}]})
        cabecalhos = {"x-hub-signature-256": assinar(corpo.encode())}

        resposta = lambda_function.lambda_handler(evento("POST", body=corpo, headers=cabecalhos), None)

        self.assertEqual(200, resposta["statusCode"])
        _sqs_fake.send_message.assert_called_once()
        enviado = _sqs_fake.send_message.call_args.kwargs
        self.assertEqual(QUEUE_URL, enviado["QueueUrl"])
        # O envelope precisa bater com WhatsAppWebhookMensagem no C# — "payload" minusculo.
        self.assertEqual({"payload": corpo}, json.loads(enviado["MessageBody"]))

    def test_assinatura_invalida_nao_publica_nada(self):
        corpo = json.dumps({"entry": []})
        cabecalhos = {"x-hub-signature-256": assinar(corpo.encode(), segredo="outro-segredo")}

        resposta = lambda_function.lambda_handler(evento("POST", body=corpo, headers=cabecalhos), None)

        self.assertEqual(403, resposta["statusCode"])
        _sqs_fake.send_message.assert_not_called()

    def test_sem_cabecalho_de_assinatura_e_recusado(self):
        resposta = lambda_function.lambda_handler(evento("POST", body="{}", headers={}), None)

        self.assertEqual(403, resposta["statusCode"])
        _sqs_fake.send_message.assert_not_called()

    def test_corpo_em_base64_e_decodificado_antes_de_conferir_a_assinatura(self):
        corpo = json.dumps({"entry": [{"id": "1"}]})
        cabecalhos = {"x-hub-signature-256": assinar(corpo.encode())}
        codificado = base64.b64encode(corpo.encode()).decode()

        resposta = lambda_function.lambda_handler(
            evento("POST", body=codificado, headers=cabecalhos, base64_encoded=True), None)

        self.assertEqual(200, resposta["statusCode"])
        self.assertEqual({"payload": corpo}, json.loads(_sqs_fake.send_message.call_args.kwargs["MessageBody"]))

    def test_corpo_alterado_apos_assinado_e_recusado(self):
        original = json.dumps({"entry": [{"id": "1"}]})
        cabecalhos = {"x-hub-signature-256": assinar(original.encode())}
        adulterado = json.dumps({"entry": [{"id": "2"}]})

        resposta = lambda_function.lambda_handler(evento("POST", body=adulterado, headers=cabecalhos), None)

        self.assertEqual(403, resposta["statusCode"])
        _sqs_fake.send_message.assert_not_called()


class MetodoNaoSuportado(unittest.TestCase):
    def test_delete_devolve_405(self):
        self.assertEqual(405, lambda_function.lambda_handler(evento("DELETE"), None)["statusCode"])


if __name__ == "__main__":
    unittest.main()
