# Git Workflow

## Branches

- **`dev`**: branch de integração. Auto-deploy pra ambiente dev a cada merge. Todo trabalho
  novo parte daqui.
- **`master`**: protegido. Só recebe merge via PR aprovado (policy configurada no Azure
  Repos) — nunca push direto. Deploy de prod.

## Branch / worktree por tarefa

Nome padrão: **`GiomCaptionVideo-[XXXX]/[XXXX]-descrição-curta`**, onde `XXXX` é o número do
work item no Azure Boards (User Story ou Feature). Exemplo real:
`GiomCaptionVideo-32/32-fila-ingestao-desacoplamento`.

Trabalho maior/experimental usa `git worktree` em vez de `checkout` direto, mantendo o
diretório principal limpo em `dev`:
```bash
git worktree add ../GiomCaptionVideos-worktrees/32-fila-ingestao-desacoplamento \
  -b GiomCaptionVideo-32/32-fila-ingestao-desacoplamento dev
```

Sempre parte de `dev` atualizado (`git fetch origin dev` antes de criar a branch, ou
`git rebase origin/dev` se a branch já existir e `dev` tiver avançado).

**Branch empilhada**: quando uma Feature depende de código de outra ainda não mergeada em
`dev` (ex: Feature #34 — DLQ/retry — depende das filas por etapa da Feature #33), a nova
worktree/branch parte da branch da feature anterior, não de `dev`, e a PR é aberta contra
essa branch (não contra `dev`). Quando a PR de base mesclar em `dev`, a PR empilhada passa a
apontar pra `dev` automaticamente.

## Pull Requests

- Sempre contra `dev` (nunca direto em `master`).
- Título: `[DEV/MASTER][Número do work item] - Mensagem explicativa real`.
- Corpo segue o template em `.azuredevops/pull_request_template.md` (contexto, checklist de
  boas práticas, o que foi alterado, work items relacionados).
- **PR fechado não reabre** — se surgir um ajuste depois que o PR já foi mergeado, é um PR
  novo (mesma branch de origem serve, se ainda existir).
- Criação/atualização via `az repos pr create` / `az repos pr update` (Azure DevOps CLI).
  Atenção a acentuação: a Azure DevOps REST API rejeita descrição que termine exatamente em
  uma referência de work item sem nada depois (ex: `...#40` no fim da string) — sempre fechar
  a descrição com quebra de linha ou pontuação.

## Commits

- Mensagem no imperativo, foco no "porquê" quando não é óbvio pelo diff.
- Preferir commit novo a `--amend`, exceto quando o usuário pedir explicitamente amend.
- Nunca `--no-verify` ou pular hooks sem pedido explícito.

## Ações destrutivas / infraestrutura

- `git push --force`, `reset --hard`, deletar branch: nunca sem confirmação explícita.
- Provisionamento de infraestrutura real (criar fila SQS, alterar policy IAM, rodar `ecs
  run-task` manual) segue o mesmo princípio — confirmar antes de executar, mesmo quando a
  intenção já pareceu combinada numa mensagem anterior.
