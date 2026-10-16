# Reconexão por canal (Next.js na Vercel)

Página restrita onde o Mauro (1) conecta o YouTube de cada canal via OAuth e (2) atualiza os
cookies do fallback yt-dlp — sem redeploy, sem root, sem colar segredo à mão. O pipeline .NET
continua na AWS; este app **não** reusa adapters .NET.

- Grava em `${SSM_PREFIX}/canais/{secrets_key}/Google/RefreshToken` (lido no .NET como
  `{secrets_key}:Google:RefreshToken`) e `${SSM_PREFIX}/canais/{secrets_key}/YtDlp/CookiesContent`.
- Lista de canais = `canal_ingestao` (ativos), via PostgREST com a role `reconexao_leitura`
  (criada pelo **PR-03b**, ver abaixo). Canal novo = linha nova na tabela, sem deploy.
- O app **nunca descriptografa** nada: `GetParameter` sempre com `WithDecryption=false`, e a
  policy IAM não tem `kms:Decrypt`.

## Ordem de merge: PR-03b antes do PR-03

Este PR (PR-03) só toca em `web/**`. A infra que ele depende vai num PR separado, o
**PR-03b: separar pipelines + role de leitura**, que:

- adiciona `trigger.paths.exclude: web/**` ao `azure-pipelines.yml` **raiz** (.NET + migrations),
  para que mexer na página nunca builde o .NET (e vice-versa: o pipeline desta pasta só dispara
  com `web/reconexao/**`);
- traz a migration `supabase/migrations/20261008120000_reconexao_leitura_canal_ingestao.sql`
  (role `reconexao_leitura`, NOLOGIN, SELECT só em `channel_id`, `nome`, `secrets_key`, `ativo`
  e policy RLS só de leitura de canais ativos), aplicada pelo pipeline raiz via `supabase db push`.

**O PR-03b entra primeiro.** Sem o exclude na raiz, o merge do PR-03 (`web/**`) ainda dispararia
o build .NET e as migrations. A página também só consegue listar canais depois que a role existe
e o JWT dela foi gerado (ver "Setup de infra").

## Variáveis de ambiente

Todas validadas no boot (`src/instrumentation.ts`); falta/valor inválido → erro listando só os
**nomes**. Vercel: **Preview = dev**, **Production = prod**. Modelo em `.env.example`.

| Variável | Preview (dev) | Production (prod) |
|---|---|---|
| `SESSION_SECRET` (≥ 32 chars, aleatório) | distinto | distinto |
| `ALLOWED_EMAILS` (csv) | e-mails autorizados | idem |
| `AUTH_GOOGLE_CLIENT_ID` / `AUTH_GOOGLE_CLIENT_SECRET` (sign-in, `openid email`) | client de sign-in | idem (pode ser o mesmo) |
| `AUTH_REDIRECT_URI` | `https://<alias-preview>/api/auth/callback` | `https://<dominio-prod>/api/auth/callback` |
| `YOUTUBE_GOOGLE_CLIENT_ID` / `YOUTUBE_GOOGLE_CLIENT_SECRET` | **iguais** aos do pipeline dev | **iguais** aos do pipeline prod |
| `YOUTUBE_REDIRECT_URI` | `https://<alias-preview>/api/youtube/callback` | `https://<dominio-prod>/api/youtube/callback` |
| `OAUTH_APP_MODE` (`testing`\|`production`) | conforme o app no Google Cloud | idem |
| `OAUTH_TESTING_EXPIRY_DAYS` (opcional, padrão 7) | 7 | 7 |
| `OAUTH_REVOKE_ON_MISMATCH` (opcional, padrão `false`) | `false` | `false` (ver trade-off em "Segurança") |
| `RECONEXAO_AWS_REGION` | `us-east-1` | `us-east-1` |
| `RECONEXAO_AWS_ACCESS_KEY_ID` / `RECONEXAO_AWS_SECRET_ACCESS_KEY` | user `giom-dev-reconexao-vercel` | user `giom-prod-reconexao-vercel` |
| `SSM_PREFIX` | `/giom/dev` | `/giom/prod` |
| `SSM_KMS_KEY_ID` | KMS do ambiente dev | KMS do ambiente prod |
| `SSM_COOKIES_TIER` (`Standard` 4 KB \| `Advanced` 8 KB) | `Standard`; Advanced exige `kms:GenerateDataKey` na policy, é irreversível e tem custo mensal (ver "Policy IAM mínima") | idem |
| `CANAIS_API_URL` (origem https; http só em localhost) | branch `dev` do Supabase: `https://lusfwbiiuxyzduniptes.supabase.co` | origem do projeto Supabase: `https://<ref>.supabase.co` |
| `CANAIS_API_REST_PREFIX` (opcional, padrão vazio) | `/rest/v1` | `/rest/v1` |
| `CANAIS_API_TOKEN` (**obrigatório**) | JWT da role `reconexao_leitura` na fonte de dev | JWT da role `reconexao_leitura` no Supabase de prod |
| `CANAIS_API_KEY` (opcional) | publishable key da branch dev | publishable/anon key do projeto (o gateway do Supabase exige o header `apikey`) |

`AUTH_REDIRECT_URI` e `YOUTUBE_REDIRECT_URI` precisam ter a mesma origem. O prefixo
`RECONEXAO_` existe porque a Vercel reserva nomes `AWS_*` no runtime.

### Fonte de canais (`CANAIS_API_*`)

As variáveis espelham o `Supabase:Url` / `Supabase:RestPrefix` dos workers .NET: a URL final é
`CANAIS_API_URL + CANAIS_API_REST_PREFIX + /canal_ingestao?select=channel_id,nome,secrets_key&ativo=eq.true`.
A URL de dev acima já consta no `appsettings.Development.json` dos workers; **nenhum valor de URL
está no código** — tudo vem de env var.

- `CANAIS_API_TOKEN` é sempre enviado como `Authorization: Bearer`. O PostgREST de dev aceita
  leitura anônima, mas o app **não depende disso**: sem o token ele nem sobe (erro de config
  listando só o nome) e nunca faz requisição sem o Bearer.
- `apikey` só é enviado quando `CANAIS_API_KEY` está definido. Nunca se manda o JWT da role como
  `apikey` (o gateway do Supabase recusaria).
- 401/403 da fonte viram o log `fonte_canais_negou` (só o status, sem URL nem token) e a página
  mostra um erro genérico.

## Rodar local

Requer Node 24 (`engines: 24.x`, exigido pela Vercel).

```bash
cd web/reconexao
cp .env.example .env.local     # preencher (nunca commitar)
npm ci
npm run dev                    # http://localhost:3000
npm run lint && npm run typecheck && npm test && npm run build
```

Local exige redirect URIs `http://localhost:3000/api/...` cadastrados nos dois clients Google
(`http://localhost` é aceito pela validação). Os testes não usam rede nem credenciais.

## Client OAuth do YouTube (Web application)

Os workers geravam o refresh token pelo fluxo de **app instalado**: no `dev` antigo,
`GoogleWebAuthorizationBroker.AuthorizeAsync` com `FileDataStore` usa o `LocalServerCodeReceiver`,
que abre o navegador e recebe o `code` por loopback (`http://127.0.0.1:<porta>/authorize/`). Esse
fluxo só funcionava na máquina local e o PR-01 o removeu. Por isso o client de dev (`1069116336751-…`)
é do tipo **Desktop/Installed**, que não tem campo de redirect URI e **não serve** para esta página.

Para a página funcionar é preciso criar um client **Web application** no projeto Google do
ambiente:

1. Google Cloud > APIs e serviços > Credenciais > criar client **Web application**, com a
   redirect URI exata `https://<alias-preview>/api/youtube/callback` (alias sugerido para dev:
   `giom-reconexao-dev.vercel.app`, projeto Vercel `giom-reconexao`; em prod, o domínio de
   produção). No código a URI vem sempre de `YOUTUBE_REDIRECT_URI`.
2. Esse ClientId/ClientSecret vão para `YOUTUBE_GOOGLE_CLIENT_ID/SECRET` na Vercel.
3. **O refresh token é amarrado ao client que o emitiu.** Os workers do mesmo ambiente precisam
   passar a usar **esse mesmo** ClientId e ClientSecret:
   - ClientId: com o PR-01 (!93, `feature/pr01-credencial-por-canal`), o .NET lê
     `{secrets_key}:Google:ClientId` (SSM `/giom/{env}/canais/{secrets_key}/Google/ClientId`) se
     existir e, senão, cai no compartilhado `Google:ClientId`. (Em `dev`, base desta branch, o
     código ainda lê só `Google:ClientId`.)
     - **Caminho recomendado** para o client Web compartilhado pelos dois canais: trocar
       `Google:ClientId` no `appsettings.Development.json` da Ingestão e da Transcrição. É mudança
       de config a fazer **depois** que o client Web existir (o valor ainda não existe).
     - **Alternativa:** gravar `/giom/dev/canais/{secrets_key}/Google/ClientId` no SSM, um por
       canal.
   - ClientSecret: SSM `/giom/{env}/canais/{secrets_key}/Google/ClientSecret` (um por canal; com
     um client compartilhado, o mesmo valor nos dois). Esta página não grava o ClientSecret
     (a policy IAM dela não permite): é passo manual de infra.
4. **Os tokens antigos, emitidos pelo client Desktop, deixam de servir** quando o ClientId do
   worker mudar (`invalid_grant`). Reconecte os **dois** canais pela página depois da troca.
5. Rastreabilidade: o `ClientId` (identificador público, não segredo) é gravado na `Description`
   do parâmetro SSM (`ClientId=…`) e aparece no log `conectado`. Compare com o log de boot dos
   workers; se divergirem, o teste pós-conexão acusa `token_invalido`.

Ao rotacionar o ClientSecret, atualize **os dois lados** (Vercel e SSM) e reconecte os canais.

### Escopos

A página pede só `https://www.googleapis.com/auth/youtube.force-ssl`, que cobre
`captions.list`/`captions.download` e as leituras de `channels`/`playlistItems`/`videos` que o
pipeline faz. O `GoogleAuthorizationCodeFlow` do .NET lista também `youtube`, `youtube.readonly`,
`cloud-platform` e `cloud-translation`; levantamento no código .NET desta branch:

- Pacotes Google usados: só `Google.Apis.Auth` e `Google.Apis.YouTube.v3`. **Não há pacote nem
  chamada de Cloud Translation ou de outra API Google** além do YouTube; `cloud-platform` e
  `cloud-translation` são resquício e nenhum worker usa a credencial do usuário para elas.
- Chamadas feitas com a credencial do usuário: `Channels.List`, `PlaylistItems.List`,
  `Videos.List` e `Captions.List`/`Captions.Download` — todas cobertas por `youtube.force-ssl`.
- O refresh do SDK (`UserCredential.RefreshTokenAsync`) não reenvia a lista de escopos: a lista
  `scopes` do flow só é usada para montar a URL de autorização, que no worker headless nunca
  ocorre. (Este último ponto vem do comportamento conhecido do SDK `Google.Apis.Auth` 1.75; o
  código-fonte do SDK não está no repositório.) O refresh devolve os escopos originalmente
  concedidos — aqui, só `youtube.force-ssl`.
- Consequência: como o pipeline é só YouTube, o token com escopo único basta. Se um dia entrar
  tradução ou outra API Google com credencial do usuário, o escopo novo precisa ser pedido
  nesta página e os canais reconectados.

## Setup de infra (feito pelo Mauro — nada aqui é automatizado)

1. **Google Cloud**: client OAuth **Web application** do YouTube (ver seção acima) com o redirect
   URI de `YOUTUBE_REDIRECT_URI`; um segundo client de sign-in com `AUTH_REDIRECT_URI`.
2. **Fonte de canais**: a role `reconexao_leitura` vem do **PR-03b** (migration aplicada pelo
   pipeline raiz no Supabase; PR-03b merge antes). Depois, gerar o **JWT da role**
   (payload `{"role":"reconexao_leitura","iss":"supabase","iat":…,"exp":…}`) assinado com o JWT
   secret do projeto e colocar em `CANAIS_API_TOKEN`. Nunca usar service_role/anon como token.
   No PostgREST de dev (Railway) o JWT precisa ser aceito pelo mesmo segredo configurado lá.
3. **IAM**: user `giom-{env}-reconexao-vercel` com a policy mínima abaixo.
4. **Vercel**: projeto com **Root Directory vazio** (o pipeline roda o CLI dentro de
   `web/reconexao`), env vars por ambiente, domínio fixo de Preview (alias) e domínio de prod.
   Deployment Protection da Vercel no Preview deve permitir o retorno do Google (o app tem auth
   própria).
5. **ADO**: registrar `web/reconexao/azure-pipelines.yml` (Pipelines > New > Existing YAML);
   criar o variable group `giom-reconexao` (`VERCEL_TOKEN` secreto, `VERCEL_ORG_ID`,
   `VERCEL_PROJECT_ID`, `RECONEXAO_PREVIEW_ALIAS` e, opcional, `VERCEL_SCOPE` — slug do time,
   passado como `--scope` só quando definido; **não definir com token de time/projeto**, pois
   `--scope` faz o CLI chamar `/v2/user`; o `VERCEL_ORG_ID` já seleciona o time) e o environment `reconexao-prod` com aprovação
   manual.

### Policy IAM mínima

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["ssm:PutParameter", "ssm:GetParameter"],
      "Resource": [
        "arn:aws:ssm:us-east-1:<conta>:parameter/giom/{env}/canais/*/Google/RefreshToken",
        "arn:aws:ssm:us-east-1:<conta>:parameter/giom/{env}/canais/*/YtDlp/CookiesContent"
      ]
    },
    {
      "Effect": "Allow",
      "Action": "kms:Encrypt",
      "Resource": "arn:aws:kms:us-east-1:<conta>:key/<key-id-do-ambiente>",
      "Condition": { "StringEquals": { "kms:ViaService": "ssm.us-east-1.amazonaws.com" } }
    }
  ]
}
```

Esta policy cobre `SSM_COOKIES_TIER=Standard` (SecureString Standard exige `kms:Encrypt` no
`PutParameter`).

**Variante para `SSM_COOKIES_TIER=Advanced`:** SecureString Advanced usa envelope encryption e
exige `kms:GenerateDataKey` na mesma chave. Troque a ação do statement de KMS por
`["kms:Encrypt", "kms:GenerateDataKey"]` (mesmo `Resource`, mesma condição `kms:ViaService`);
continua **sem** `kms:Decrypt`:

```json
{
  "Effect": "Allow",
  "Action": ["kms:Encrypt", "kms:GenerateDataKey"],
  "Resource": "arn:aws:kms:us-east-1:<conta>:key/<key-id-do-ambiente>",
  "Condition": { "StringEquals": { "kms:ViaService": "ssm.us-east-1.amazonaws.com" } }
}
```

O tier Advanced é irreversível no parâmetro (não volta para Standard) e tem custo mensal por
parâmetro; só vale usar se o `cookies.txt` filtrado passar de 4 KB. Sem `kms:GenerateDataKey`, o
`PutParameter` dos cookies em Advanced falha com `AccessDenied`.

## Segurança

- Acesso: sign-in Google separado (`openid email`), sessão em cookie `__Host-` httpOnly/Secure/
  Lax assinado (8 h), allowlist re-checada a cada requisição. Fora da allowlist → 403 em todas as
  rotas, inclusive `/api/youtube/callback`; checado no proxy (`src/proxy.ts`, antigo middleware) **e** em cada handler. Únicas
  rotas sem sessão: `/api/auth/login` e `/api/auth/callback` (este responde 403 a quem não está
  na allowlist). Navegação (HTML) sem sessão é redirecionada ao sign-in; chamadas de API sem
  sessão recebem 403. POSTs exigem `Origin` igual à origem pública.
- OAuth do YouTube: `access_type=offline`, `prompt=consent`, sem `include_granted_scopes`, escopo
  `youtube.force-ssl`, `state` + PKCE S256 num cookie AES-GCM de uso único (10 min) amarrado à
  sessão e ao canal. O callback sempre redireciona para `/` **sem** `code`/`state`; o resultado
  viaja só como códigos classificados na query.
- Permissão validada antes de gravar (`channels.list?part=id&mine=true` deve conter o
  `channel_id`) e o escopo tem de ser o pedido; senão **nada é gravado**. Padrão
  (`OAUTH_REVOKE_ON_MISMATCH=false`): a página só recusa e avisa que nada foi gravado nem
  revogado, pedindo para entrar com a conta dona do canal. **Por quê:** os dois canais usam o
  mesmo client OAuth e o Google revoga o grant inteiro da conta para esse client, então revogar
  poderia matar o token vivo de **outro** canal. O token obtido pela conta errada fica ativo até
  expirar ou ser removido em myaccount.google.com/permissions.
  Com `OAUTH_REVOKE_ON_MISMATCH=true` (opt-in explícito) o token obtido também é revogado; a
  mensagem diz se houve revogação e a página avisa, junto do botão, que entrar com a conta de
  outro canal pode revogar a autorização dessa conta. Revogação nunca ocorre em falha de
  SSM/rede nem na ausência de refresh token.
- Segredos de vida curta (access/refresh token do request) ficam registrados para redação só
  durante o handler do callback (`createSecretScope`, liberado em `finally`); só os segredos
  estáticos da env permanecem registrados no processo.
- Scrubbing: `src/lib/redact.ts` + `src/lib/log.ts` (único ponto de log). Nunca se loga nem se
  devolve `code`, `state`, tokens, ClientSecret ou conteúdo de cookies; erros saem sem stack.
- Headers: CSP com nonce (proxy), `X-Frame-Options: DENY`, `Referrer-Policy: same-origin`
  (Origin real no POST same-origin; nada vaza para o Google),
  `Cache-Control: no-store`, HSTS, `nosniff`.

### Se ligar Sentry depois

Adicionar `beforeSend` e `beforeSendTransaction` que removam a query string e dados sensíveis:
em eventos/spans `http.server` e `http.client`, apagar `request.query_string`, `request.cookies`,
`request.headers.cookie|authorization` e cortar `?...` de `url`/`http.url`/`http.query`/`transaction`;
passar mensagens de exceção por `redact()`; `sendDefaultPii: false`; desligar captura de body.
O `code` do OAuth chega na URL do callback, então sem esse filtro ele vazaria como atributo de span.

## Pipeline

`azure-pipelines.yml` (neste diretório): trigger em `dev`/`master` só com
`paths.include: web/reconexao/**`. O pipeline raiz (.NET) exclui `web/**` (PR-03b), então mexer
na página nunca builda o .NET e mexer no .NET nunca builda a página. Build (npm ci, lint, typecheck, test, next build) →
`dev`: `vercel pull --environment=preview` → `vercel build` → `vercel deploy --prebuilt --target=preview`
→ espera READY e confere o target via REST API (`GET /v13/deployments`; falha o job se não for Preview) → alias via REST (`POST /v2/deployments/{id}/aliases`). `inspect`/`alias` não usam o CLI porque ele chama `GET /v2/user`, que dá "User not found" com token de time/projeto;
`master`: `vercel pull --environment=production` → `vercel build --prod` →
`vercel deploy --prebuilt --prod` num deployment job do environment `reconexao-prod`. O build do
deploy roda no agente (Node 24, `UseNode@1`); `.vercel/` (env pulada) é gitignored e nunca é impressa.
Outras branches só buildam/testam.

**Armadilha do primeiro deploy:** a Vercel trata o PRIMEIRO deployment de um projeto como produção
mesmo sem `--prod` (doc do `vercel deploy`, seção Prod). No CLI isso só ocorre quando não há
`--target` explícito. Se esse primeiro deploy falha, o projeto continua sem produção e o próximo
deploy "implícito" viraria produção de novo. Por isso o Preview usa `--target=preview` e confere o
target antes de aplicar o alias. Vercel CLI com versão fixa
(`vercelCliVersion: 63.1.0`; versões antigas, como a 48.x, não leem `VERCEL_TOKEN` do ambiente
e podem cair no prompt de login). O token vai só por `VERCEL_TOKEN` (env), nunca por flag. O pipeline **precisa ser registrado no ADO pelo Mauro**.

O `.gitignore` do app é `web/reconexao/.gitignore` (o da raiz não foi alterado).
