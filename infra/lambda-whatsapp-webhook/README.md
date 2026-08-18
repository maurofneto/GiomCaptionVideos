# Webhook do WhatsApp (Task #140)

Recebe o que a Meta empurra e coloca na fila que o bot consome.

```
Meta → Function URL (HTTPS) → Lambda → SQS → AgenteInstituto.Worker
                               │                    │
                        confere assinatura    WhatsAppWebhookProcessor (C#)
```

## Por que Lambda, e não um endpoint no próprio bot

A Meta só entrega webhook em HTTPS com certificado de CA e num endereço estável. O serviço
ECS do bot não expõe porta nenhuma (Security Group sem regra de entrada) e o IP público da
task Fargate muda a cada deploy. Abrir isso exigiria um ALB — **US$ 16,43/mês fixos**, mais
domínio e certificado, numa conta que gasta ~US$ 33/mês no total.

A Function URL da Lambda já vem com HTTPS, certificado válido e URL fixa, sem custo relevante
nesse volume, e o container do bot continua sem porta aberta pra internet.

Ganho extra: como o transporte virou fila, a pergunta do aluno **não se perde** se o bot cair
ou reiniciar no meio de um deploy — ela espera na fila. Entrega HTTP direta no container não
daria isso.

## A Lambda é burra de propósito

Ela confere a assinatura e repassa o corpo **cru**. Quem entende o formato do payload é o
`WhatsAppWebhookProcessor`, no C#. Assim, quando a Meta mudar o formato, só o C# muda.

O único controle de acesso do endpoint (que é público) é a validação de
`X-Hub-Signature-256` — HMAC-SHA256 do corpo cru com o App Secret, comparado com
`hmac.compare_digest`. Comparação com `==` vazaria, pelo tempo de resposta, quantos caracteres
iniciais bateram. Os testes cobrem assinatura válida, inválida, ausente, corpo em base64 e
corpo adulterado depois de assinado.

## Rodar

Testes (não tocam a AWS — `boto3` é stubado):

```bash
python -m unittest -v
```

Provisionar (idempotente; cria fila+DLQ, role IAM, Lambda e Function URL):

```bash
powershell -File provisionar.ps1 -Ambiente dev
```

Antes de provisionar, os secrets precisam existir no SSM — o script lê de lá e nunca grava:

- `/giom/{dev,prod}/agenteinstituto/WhatsApp/VerifyToken` — você inventa; o mesmo valor vai no painel da Meta
- `/giom/{dev,prod}/agenteinstituto/WhatsApp/AppSecret` — copiado do app no Meta Business

Depois de rodar, cadastre a Function URL exibida no painel da Meta como webhook, com o mesmo
`VerifyToken`, e assine o campo `messages`.

## Estado atual

O caminho de entrada está completo, mas o canal WhatsApp nasce **desligado**
(`WhatsApp:Habilitado = false` no appsettings do bot). O `ChatBotProcessor` ainda escuta um
único `IChatChannel`, que é o Telegram — então pergunta livre vinda do WhatsApp chegaria ao
inbox e ficaria sem leitor. Comando (`/ajuda`) e clique de feedback já funcionam ponta a ponta,
porque o processor responde direto.

Quem fecha isso é a **Task #141** (um `ChatBotProcessor` por canal). Só então vale ligar a flag.

## Testar sem provisionar nada

Pra validação em dev (Task #142) não é preciso subir Lambda: um túnel (ngrok, Cloudflare
Tunnel) dá HTTPS válido apontando pra máquina local e permite exercitar o fluxo inteiro antes
de criar infra na AWS.
