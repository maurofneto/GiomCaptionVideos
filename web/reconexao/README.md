# Reconexão por canal (Next.js na Vercel)

Página restrita onde o Mauro (1) conecta o YouTube de cada canal via OAuth e (2) atualiza os
cookies do fallback yt-dlp — sem redeploy, sem root, sem colar segredo à mão. O pipeline .NET
continua na AWS; este app **não** reusa adapters .NET.

- Grava em `${SSM_PREFIX}/canais/{secrets_key}/Google/RefreshToken` (lido no .NET como
  `{secrets_key}:Google:RefreshToken`) e `${SSM_PREFIX}/canais/{secrets_key}/YtDlp/CookiesContent`.
- Lista de canais = `canal_ingestao` (ativos), via PostgREST com a role `reconexao_leitura`.
  Canal novo = linha nova na tabela, sem deploy.
- O app **nunca descriptografa** nada: `GetParameter` sempre com `WithDecryption=false`, e a
  policy IAM não tem `kms:Decrypt`.

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
| `OAUTH_REVOKE_ON_MISMATCH` (opcional, padrão `true`) | `true` | `true` (ver trade-off abaixo) |
| `RECONEXAO_AWS_REGION` | `us-east-1` | `us-east-1` |
| `RECONEXAO_AWS_ACCESS_KEY_ID` / `RECONEXAO_AWS_SECRET_ACCESS_KEY` | user `giom-dev-reconexao-vercel` | user `giom-prod-reconexao-vercel` |
| `SSM_PREFIX` | `/giom/dev` | `/giom/prod` |
| `SSM_KMS_KEY_ID` | KMS do ambiente dev | KMS do ambiente prod |
| `SSM_COOKIES_TIER` (`Standard` 4 KB \| `Advanced` 8 KB) | `Standard`; Advanced exige `kms:GenerateDataKey` na policy, é irreversível e tem custo mensal (ver "Policy IAM mínima") | idem |
| `SUPABASE_URL` | projeto dev | projeto prod |
| `SUPABASE_RECONEXAO_JWT` | JWT da role no projeto dev | JWT da role no projeto prod |

`AUTH_REDIRECT_URI` e `YOUTUBE_REDIRECT_URI` precisam ter a mesma origem. O prefixo
`RECONEXAO_` existe porque a Vercel reserva nomes `AWS_*` no runtime.

## Rodar local

```bash
cd web/reconexao
cp .env.example .env.local     # preencher (nunca commitar)
npm ci
npm run dev                    # http://localhost:3000
npm run lint && npm run typecheck && npm test && npm run build
```

Local exige redirect URIs `http://localhost:3000/api/...` cadastrados nos dois clients Google
(`http://localhost` é aceito pela validação). Os testes não usam rede nem credenciais.

## Sincronia de ClientId/ClientSecret (importante)

O refresh token é **amarrado ao client OAuth que o emitiu**. Por isso
`YOUTUBE_GOOGLE_CLIENT_ID/SECRET` na Vercel devem ser **idênticos** aos que Ingestão e
Transcrição usam no mesmo ambiente (`Google:ClientId` no `appsettings.json` e
`{secrets_key}:Google:ClientSecret` no SSM). Se divergirem, o teste pós-conexão acusa
`invalid_grant`/`token_invalido`. Ao rotacionar o ClientSecret, atualize **os dois lados** (Vercel
e SSM) e reconecte os canais.

Atenção: no .NET o ClientSecret é lido **por canal** (`/giom/{env}/canais/{secrets_key}/Google/ClientSecret`).
A página usa um único secret por ambiente; isto só funciona se os canais compartilharem o mesmo
client (hoje o ClientId já é compartilhado). Confirmar antes de usar em prod.

## Setup de infra (feito pelo Mauro — nada aqui é automatizado)

1. **Google Cloud**: client OAuth do YouTube (o mesmo do pipeline) com o redirect URI de
   `YOUTUBE_REDIRECT_URI`; um segundo client de sign-in com `AUTH_REDIRECT_URI`.
2. **Supabase**: a migration `supabase/migrations/20261008120000_reconexao_leitura_canal_ingestao.sql`
   é aplicada pelo pipeline no merge. Depois gerar o **JWT da role `reconexao_leitura`**
   (payload `{"role":"reconexao_leitura","iss":"supabase","iat":…,"exp":…}`) assinado com o JWT
   secret do projeto e colocar em `SUPABASE_RECONEXAO_JWT`. Nunca usar service_role/anon aqui.
3. **IAM**: user `giom-{env}-reconexao-vercel` com a policy mínima abaixo.
4. **Vercel**: projeto com **Root Directory vazio** (o pipeline roda o CLI dentro de
   `web/reconexao`), env vars por ambiente, domínio fixo de Preview (alias) e domínio de prod.
   Deployment Protection da Vercel no Preview deve permitir o retorno do Google (o app tem auth
   própria).
5. **ADO**: registrar `web/reconexao/azure-pipelines.yml` (Pipelines > New > Existing YAML);
   criar o variable group `giom-reconexao` (`VERCEL_TOKEN` secreto, `VERCEL_ORG_ID`,
   `VERCEL_PROJECT_ID`, `RECONEXAO_PREVIEW_ALIAS` e, opcional, `VERCEL_SCOPE` — slug do time,
   passado como `--scope` só quando definido) e o environment `reconexao-prod` com aprovação
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
  rotas, inclusive `/api/youtube/callback`; checado no middleware **e** em cada handler. Únicas
  rotas sem sessão: `/api/auth/login` e `/api/auth/callback` (este responde 403 a quem não está
  na allowlist). Navegação (HTML) sem sessão é redirecionada ao sign-in; chamadas de API sem
  sessão recebem 403. POSTs exigem `Origin` igual à origem pública.
- OAuth do YouTube: `access_type=offline`, `prompt=consent`, sem `include_granted_scopes`, escopo
  `youtube.force-ssl`, `state` + PKCE S256 num cookie AES-GCM de uso único (10 min) amarrado à
  sessão e ao canal. O callback sempre redireciona para `/` **sem** `code`/`state`; o resultado
  viaja só como códigos classificados na query.
- Permissão validada antes de gravar (`channels.list?part=id&mine=true` deve conter o
  `channel_id`); senão nada é gravado. Com `OAUTH_REVOKE_ON_MISMATCH=true` (padrão, como no
  brief) o token obtido também é revogado; a mensagem de erro diz se houve revogação.
  **Trade-off:** o Google revoga o grant inteiro da conta para este client, então entrar com a
  conta errada pode matar o token vivo de **outro** canal (a página avisa ao lado do botão).
  Com `false` nada é revogado: não há risco para tokens existentes, mas o token obtido pela
  conta errada fica ativo até expirar ou ser removido em myaccount.google.com/permissions.
  Revogação nunca ocorre em falha de SSM/rede nem na ausência de refresh token.
- Segredos de vida curta (access/refresh token do request) ficam registrados para redação só
  durante o handler do callback (`createSecretScope`, liberado em `finally`); só os segredos
  estáticos da env permanecem registrados no processo.
- Scrubbing: `src/lib/redact.ts` + `src/lib/log.ts` (único ponto de log). Nunca se loga nem se
  devolve `code`, `state`, tokens, ClientSecret ou conteúdo de cookies; erros saem sem stack.
- Headers: CSP com nonce (middleware), `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`,
  `Cache-Control: no-store`, HSTS, `nosniff`.

### Se ligar Sentry depois

Adicionar `beforeSend` e `beforeSendTransaction` que removam a query string e dados sensíveis:
em eventos/spans `http.server` e `http.client`, apagar `request.query_string`, `request.cookies`,
`request.headers.cookie|authorization` e cortar `?...` de `url`/`http.url`/`http.query`/`transaction`;
passar mensagens de exceção por `redact()`; `sendDefaultPii: false`; desligar captura de body.
O `code` do OAuth chega na URL do callback, então sem esse filtro ele vazaria como atributo de span.

## Pipeline

`azure-pipelines.yml` (neste diretório): trigger em `dev`/`master` com path `web/reconexao/**`;
Build (npm ci, lint, typecheck, test, next build) → `dev`: `vercel deploy` Preview +
`vercel alias set`; `master`: `vercel deploy --prod` num deployment job do environment
`reconexao-prod`. Outras branches só buildam/testam. Vercel CLI com versão fixa
(`vercelCliVersion: 63.1.0`; versões antigas, como a 48.x, não leem `VERCEL_TOKEN` do ambiente
e podem cair no prompt de login). O token vai só por `VERCEL_TOKEN` (env), nunca por flag. O pipeline **precisa ser registrado no ADO pelo Mauro**.
