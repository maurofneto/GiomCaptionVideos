# Runbook — cookies yt-dlp por canal (US #146)

YouTube bloqueia yt-dlp sem sessão autenticada a partir de IP de datacenter (Fargate).
Cada canal (Instituto / Ministério) deve ter **cookies Netscape próprios**, exportados de
contas Google distintas — senão um ban afeta os dois canais ao mesmo tempo.

## Paths SSM

| Ambiente | Canal | Parâmetro |
|----------|-------|-----------|
| prod | Instituto | `/giom/prod/canais/instituto/YtDlp/CookiesContent` |
| prod | Ministério | `/giom/prod/canais/ministerio/YtDlp/CookiesContent` |
| dev | Instituto | `/giom/dev/canais/instituto/YtDlp/CookiesContent` |
| dev | Ministério | `/giom/dev/canais/ministerio/YtDlp/CookiesContent` |

Fallback legado (compartilhado, evitar em prod): `/giom/{env}/pipeline/YtDlp/CookiesContent`.

O boot do `Transcricao.Worker` loga `contentSha256Prefix` por `ChannelId` — os hashes de
Instituto e Ministério **devem ser diferentes** depois da rotação.

## Como exportar (Chrome)

1. Login na conta Google dedicada daquele canal (não reutilizar a mesma nos dois).
2. Extensão tipo "Get cookies.txt LOCALLY" → exportar cookies de `youtube.com` (formato Netscape).
3. Colar o conteúdo no Parameter Store (SecureString), path da tabela acima.
4. No Windows/Git Bash: prefixar AWS CLI com `MSYS_NO_PATHCONV=1` quando o `--name` começa com `/`.

Exemplo (PowerShell / cmd — sem MSYS):

```text
aws ssm put-parameter --name "/giom/prod/canais/instituto/YtDlp/CookiesContent" --type SecureString --value file://cookies-instituto.txt --overwrite --region us-east-1
```

## Quando rotacionar

- Logs com `Sign in to confirm you're not a bot` / yt-dlp exit ≠ 0 em YtDlpCaption ou Whisper.
  Se persistir com cookies recém-exportados, ver PO token e proxy abaixo (IP de datacenter).
- Cookies expiram periodicamente (semanas/meses) — reexportar e `--overwrite` no SSM.
- Depois do put-parameter, a próxima task ECS do Transcricao já lê o valor novo no boot (não
  precisa redeploy de imagem, só nova execução do service/task).

## BloqueioBot: sem retry, direto pra DLQ

`Sign in to confirm you're not a bot` (e a página de "unusual traffic") no stderr do yt-dlp é
classificado como `BloqueioBot` — **não transitório**. Tanto `YtDlpCaption` quanto o download de
áudio do `Whisper` lançam `FalhaNaoTransitoriaException`, e o `SqsFilaConsumer` copia a
mensagem pra DLQ (`giom-videos-transcricao-{env}-dlq`) já na 1ª tentativa, em vez de reprocessar
a cadeia inteira até o `MaxReceiveCount` (6). Log `Error` "Falha não transitória (Whisper,
sign_in_to_confirm_not_a_bot) ... movida direto pra DLQ".

Depois de corrigir a causa (cookies novos, PO token, proxy), reprocessar pela DLQ. Como a cópia é
feita por `SendMessage` (não pela RedrivePolicy), o redrive precisa de **destino explícito**
(fila `giom-videos-transcricao-{env}`) — "redrive to source" do console não sabe a origem.

## Versões na imagem

O Dockerfile do `Transcricao.Worker` fixa via `ARG` (versão + SHA-256): `YTDLP_VERSION`,
`DENO_VERSION`, `BGUTIL_POT_PROVIDER_VERSION`. O `yt-dlp_linux` já embute o `yt-dlp-ejs`; o deno
é o runtime JS que ele usa pro n-challenge. No boot o worker loga:

```text
yt-dlp versão: 2026.08.19
deno versão: deno 2.9.7 (stable, release, x86_64-unknown-linux-gnu)
yt-dlp configurado: PoToken=off Proxy=off
```

Binário ausente vira só `Warning` (o worker sobe mesmo assim).

## PO token (bgutil-ytdlp-pot-provider)

O plugin está na imagem (`/etc/yt-dlp/plugins/bgutil-ytdlp-pot-provider.zip`), mas o
**servidor** que gera o token (porta 4416, imagem `brainicism/bgutil-ytdlp-pot-provider`, mesma
versão do plugin) roda fora do worker — ex.: sidecar na mesma task ECS, acessível em
`http://127.0.0.1:4416`. Provisionar o sidecar é ação de infra (confirmar antes).
O servidor não tem autenticação: nunca expor fora da task/localhost.

| Ambiente | Parâmetro | Tipo | Valor |
|----------|-----------|------|-------|
| dev | `/giom/dev/pipeline/YtDlp/PoToken/Habilitado` | String | `true` / `false` |
| dev | `/giom/dev/pipeline/YtDlp/PoToken/ProviderUrl` | String | ex.: `http://127.0.0.1:4416` |
| prod | `/giom/prod/pipeline/YtDlp/PoToken/Habilitado` | String | `true` / `false` |
| prod | `/giom/prod/pipeline/YtDlp/PoToken/ProviderUrl` | String | ex.: `http://127.0.0.1:4416` |

Só liga com os **dois** preenchidos (`Habilitado=true` e `ProviderUrl` não vazio) → yt-dlp
recebe `--extractor-args "youtubepot-bgutilhttp:base_url=<ProviderUrl>"`. Desligado, o yt-dlp
roda com `YTDLP_NO_PLUGINS=1` (comportamento idêntico ao de antes do plugin). Pra conferir que
o token foi de fato usado, rodar manualmente `yt-dlp -v <url>` e procurar
`PO Token Providers: bgutil:http-<versão>`.

## Proxy

| Ambiente | Parâmetro | Tipo |
|----------|-----------|------|
| dev | `/giom/dev/pipeline/YtDlp/Proxy` | SecureString |
| prod | `/giom/prod/pipeline/YtDlp/Proxy` | SecureString |

Formato `http://usuario:senha@host:porta` (ou `socks5://...`). Vazio/ausente = sem `--proxy`.
O valor **nunca** é logado: o boot só mostra `Proxy=on/off`, e o stderr do yt-dlp re-emitido no
console tem a URL/credencial do proxy mascarada (`***`).

As chaves novas são lidas no boot como as de cookies — depois do `put-parameter`, basta uma
nova execução da task/service.

## OAuth Google (mesmo escopo da US)

ClientSecret / RefreshToken por canal continuam em:

- `/giom/{env}/canais/instituto/Google/...`
- `/giom/{env}/canais/ministerio/Google/...`

Em prod, confirme que os RefreshTokens **não** são clones um do outro (hashes distintos).
