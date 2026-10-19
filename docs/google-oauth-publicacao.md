# Publicação do app OAuth Google (login persistente)

Objetivo: tirar o app de **Testing** → **In production**. Em Testing o refresh token expira em
7 dias; em produção não expira (só por revogação, 6 meses sem uso ou troca de senha).

> **Status (2026-10-19):** páginas publicadas em `https://giom-reconexao.vercel.app` (US #153);
> app de produção enviado para publicação. Falta: aprovação do Google, `OAUTH_APP_MODE=production`
> na Vercel e reautorizar os canais (passo 5 abaixo).

> **Atalho:** pra só ter token persistente **não precisa de verificação**. Basta clicar
> "Publish app". O app fica "não verificado" (tela de aviso no consentimento, limite de 100
> usuários) — aceitável, pois só você/os 2 canais autorizam. Os itens abaixo só são
> obrigatórios se quiser a **verificação** (some o aviso). Placeholders: `<...>`.

## 1. Passos no console (APIs e serviços → Tela de permissão OAuth)

1. Preencher "Informações do app" (seção 2).
2. Domínio autorizado: `giom-reconexao.vercel.app` (verificado no Search Console por prefixo de URL).
3. Escopos (seção 3).
4. Clicar **Publicar app** → confirmar.
5. **Reautorizar** cada canal (Instituto e Ministério, dev e prod) para gerar refresh tokens
   novos — os emitidos em Testing continuam expirando. Gravar no SSM
   (`/giom/{env}/canais/{canal}/Google/RefreshToken`) e confirmar hashes distintos.
6. Definir `OAUTH_APP_MODE=production` nas env vars **Production** da Vercel (`giom-reconexao`) e
   rodar a pipeline `GiomReconexao-Web` — senão a página de reconexão continua avisando a
   expiração de 7 dias do modo Testing.

## 2. Informações do app

| Campo | Valor |
|---|---|
| Nome do app | Giom Video Manager |
| E-mail de suporte | `<mauro.furigo@gmail.com ou e-mail do ministério>` |
| Logo (120x120 px, PNG/JPG, <1 MB) | `<logo do Instituto/Ministério Giom>` |
| Página inicial | `https://giom-reconexao.vercel.app/sobre` |
| Política de privacidade | `https://giom-reconexao.vercel.app/privacidade` (texto na seção 5) |
| Termos de serviço (opcional) | `https://giom-reconexao.vercel.app/termos` |
| Domínio autorizado | `giom-reconexao.vercel.app` (verificar no Search Console por prefixo de URL — `vercel.app` é sufixo público) |
| E-mail de contato do desenvolvedor | `<e-mail>` |
| Tipo de usuário | Externo |

## 3. Escopos usados hoje (`ServiceCollectionExtensions.cs:272`) e justificativas

Todos são escopos **sensíveis** (exigem verificação). Recomendo **remover os que não usa**
antes de publicar (menos escopos = verificação mais fácil).

| Escopo | Justificativa (colar no campo) |
|---|---|
| `youtube.readonly` | Listar vídeos e metadados dos canais do próprio Instituto/Ministério Giom para ingestão automática. |
| `youtube.force-ssl` | Baixar e consultar legendas (captions) dos vídeos dos canais próprios para indexar o conteúdo. |
| `youtube` | Gerenciar legendas dos vídeos dos canais próprios (envio da transcrição gerada). |
| `cloud-translation` | Traduzir as legendas geradas para outros idiomas via Cloud Translation API. |
| `cloud-platform` | **Provável excesso** — cloud-translation já cobre. Remover se possível. |

Sugestão de enxugamento: manter `youtube.readonly`, `youtube.force-ssl` (+ `youtube` só se o
sistema realmente grava legendas) e `cloud-translation`; tirar `cloud-platform`. Se mudar o
código, **reautorizar** os canais (escopo muda → token novo).

## 4. Texto para o campo "Como o app usa os dados" / roteiro do vídeo de demonstração

**Descrição de uso (inglês — o formulário do Google pede em inglês):**

> Giom Video Manager is an internal tool operated by Instituto Giom and Ministério Giom. It
> accesses ONLY the YouTube channels owned by these organizations, authorized by the channel
> owner. It lists the channel's videos, retrieves captions, generates transcriptions and
> translations, and indexes the text content to power a Q&A bot over the organizations' own
> teaching material. No data from other users is accessed, sold, or shared with third parties.
> Google user data is used solely to provide these features.

**Roteiro do vídeo (YouTube não listado, 2–4 min, inglês na narração ou legenda):**

1. Mostrar o ID do cliente OAuth (Console → Credenciais) e o nome do app.
2. Iniciar o fluxo de autorização: tela de consentimento exibindo o nome do app e os escopos.
3. Autorizar com a conta dona do canal.
4. Mostrar o sistema listando vídeos/legendas do canal (uso de cada escopo, um por vez).
5. Mostrar a transcrição/tradução gerada sendo usada pelo bot.
6. Mostrar onde o usuário revoga o acesso (myaccount.google.com/permissions).

## 5. Política de privacidade (rascunho — hospedar em `https://giom-reconexao.vercel.app/privacidade`)

```html
<!doctype html>
<html lang="pt-BR"><head><meta charset="utf-8"><title>Política de Privacidade — Giom Video Manager</title></head>
<body>
<h1>Política de Privacidade — Giom Video Manager</h1>
<p>Última atualização: 19/10/2026</p>

<h2>Quem somos</h2>
<p>O Giom Video Manager é uma ferramenta operada pelo Instituto Giom e pelo Ministério Giom
para processar os vídeos dos seus próprios canais do YouTube. Contato: &lt;e-mail&gt;.</p>

<h2>Dados acessados</h2>
<p>Com autorização do proprietário do canal, acessamos via API do YouTube: lista de vídeos,
metadados e legendas dos canais do Instituto Giom e do Ministério Giom. Não acessamos dados
pessoais de espectadores ou de terceiros.</p>

<h2>Como usamos</h2>
<p>Os dados são usados exclusivamente para gerar transcrições e traduções e alimentar um
assistente de perguntas e respostas sobre o conteúdo das próprias organizações.</p>

<h2>Compartilhamento</h2>
<p>Não vendemos nem compartilhamos dados de usuários do Google com terceiros, exceto
provedores de infraestrutura e IA estritamente necessários à operação (hospedagem, banco de
dados e processamento de texto), vinculados a obrigações de confidencialidade. O uso de
informações recebidas das APIs do Google segue a
<a href="https://developers.google.com/terms/api-services-user-data-policy">Política de
Dados do Usuário dos Serviços de API do Google</a>, incluindo os requisitos de Uso Limitado.</p>

<h2>Armazenamento e segurança</h2>
<p>Tokens de acesso são armazenados de forma criptografada em gerenciador de segredos
(AWS SSM Parameter Store) e nunca em repositório de código.</p>

<h2>Retenção e revogação</h2>
<p>Mantemos os dados enquanto o acesso estiver autorizado. Você pode revogar o acesso a
qualquer momento em <a href="https://myaccount.google.com/permissions">myaccount.google.com/permissions</a>
e solicitar a exclusão dos dados pelo e-mail acima.</p>
</body></html>
```

## 6. Páginas legais no ar (`web/reconexao`)

Código, `CONTATO_EMAIL` e a regra de build estático estão em
[`web/reconexao/README.md`](../web/reconexao/README.md#páginas-legais-publicação-do-app-oauth-google).
Resumo: mudar `CONTATO_EMAIL` exige rodar a pipeline (build estático); "Redeploy" da Vercel não
atualiza. A reconexão (`web/reconexao`) pede só `youtube.force-ssl`; os demais escopos vêm do
Transcrição (`ServiceCollectionExtensions.cs`) — a tela de consentimento deve declarar a união.

## 7. Pendências (só o Mauro pode fazer)

- Definir `CONTATO_EMAIL` real na Vercel e rodar a pipeline (hoje vazio: páginas mostram
  "contato indisponível"; só importa se o Google exigir verificação completa).
- Logo e vídeo de demonstração (seção 4), se for pedir verificação.
- Decidir se remove `cloud-platform`/`youtube` dos escopos.
- Pós-aprovação do Google: passos 5 e 6 da seção 1.
