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
- Cookies expiram periodicamente (semanas/meses) — reexportar e `--overwrite` no SSM.
- Depois do put-parameter, a próxima task ECS do Transcricao já lê o valor novo no boot (não
  precisa redeploy de imagem, só nova execução do service/task).

## OAuth Google (mesmo escopo da US)

ClientSecret / RefreshToken por canal continuam em:

- `/giom/{env}/canais/instituto/Google/...`
- `/giom/{env}/canais/ministerio/Google/...`

Em prod, confirme que os RefreshTokens **não** são clones um do outro (hashes distintos).
