# GitHub Actions → AWS por OIDC

O workflow [`ci-cd.yml`](../../.github/workflows/ci-cd.yml) autentica na AWS assumindo uma role
via OIDC — **sem access key guardada no GitHub**. Substitui o usuário IAM `giom-ci-deploy`.

## Como funciona

- O job com `environment: dev|prod` recebe do GitHub um token cujo `sub` é
  `repo:maurofneto@1536870/GiomCaptionVideos@1426143006:environment:<ambiente>` — o repo usa o
  **subject imutável** (ids numéricos de dono e repo, imune a rename/transfer). O prefixo certo
  vem de `gh api repos/maurofneto/GiomCaptionVideos/actions/oidc/customization/sub`.
- Cada role (`giom-github-deploy-dev` / `-prod`) só confia nesse `sub` exato. O environment, por
  sua vez, só aceita deploy da branch certa (dev → `dev`, prod → `master`) — assim um PR ou outra
  branch nunca assume a role de prod.
- Permissões de cada role: push em ECR `giom-*` + `ecs:UpdateService` **apenas nos services do
  próprio ambiente** (a dev não reinicia service de prod). A Lambda watchman e o
  `lambda:UpdateFunctionCode` não existem mais.
- Limitação herdada: os repositórios ECR são compartilhados entre dev e prod (tags `:dev` e
  `:latest`); a role dev tecnicamente consegue dar push em `:latest`. Mesmo comportamento do
  `giom-ci-deploy`. Tag immutability / políticas por tag ficam como melhoria.

## Provisionar a AWS (uma vez)

```bash
pwsh infra/github-oidc/provisionar.ps1          # dry-run: só mostra
pwsh infra/github-oidc/provisionar.ps1 -Apply   # altera IAM de verdade
```

## Configurar o GitHub (uma vez)

Variáveis do **repositório** (Settings → Secrets and variables → Actions → Variables):

| Variável | Valor |
|---|---|
| `AWS_REGION` | `us-east-1` |
| `ECR_REGISTRY` | `<conta>.dkr.ecr.us-east-1.amazonaws.com` |
| `DEPLOY_ENABLED` | `true` só depois do cutover do Azure (interruptor) |

Environments `dev` e `prod` (Settings → Environments). Restrinja *Deployment branches* a `dev` e
`master`, respectivamente, e coloque *Required reviewers* no `prod` se quiser portão manual.
Cada um com:

| Variável | dev | prod |
|---|---|---|
| `AWS_ROLE_ARN` | `…:role/giom-github-deploy-dev` | `…:role/giom-github-deploy-prod` |
| `IMAGE_TAG` | `dev` | `latest` |
| `SUPABASE_PROJECT_REF` | ref do branch dev | ref do projeto prod |
| `AGENTEINSTITUTO_SERVICE` | `giom-agenteinstituto-dev-service` | `giom-agenteinstituto-service` |
| `TRANSCRICAO_SERVICE` / `RESUMO_SERVICE` / `CATEGORIZACAO_SERVICE` / `EMBEDDING_SERVICE` / `ORQUESTRACAO_SERVICE` | `giom-<etapa>-dev-service` | `giom-<etapa>-service` |

Secret do environment (não vai em arquivo nem em chat — peça ao dono pra gravar no terminal):

```bash
gh secret set SUPABASE_ACCESS_TOKEN --env dev  -R maurofneto/GiomCaptionVideos
gh secret set SUPABASE_ACCESS_TOKEN --env prod -R maurofneto/GiomCaptionVideos
```

## Cutover

1. Aplicar IAM + configurar variáveis/secrets/environments.
2. Rodar o workflow manualmente (`workflow_dispatch`) em `dev` com `DEPLOY_ENABLED=true` e
   conferir imagens no ECR e services reiniciados.
3. Desligar o trigger do Azure Pipelines, remover `azure-pipelines.yml` e
   `pipelines/templates/`.
4. Desativar e depois apagar as access keys do usuário `giom-ci-deploy`.
5. Exigir o check `Build e testes` nos rulesets de `dev` e `master`.
