---
name: criar-pr-template
description: Cria ou atualiza um Pull Request no GitHub deste projeto (maurofneto/GiomCaptionVideos) seguindo o template padrão do time. Use sempre que o usuário pedir pra "mandar o PR", "abrir PR", "atualizar a descrição do PR", "criar pull request", "sobe isso aí" depois de um trabalho pronto, "retargeta o PR", ou quando uma branch estiver pronta pra revisão — sempre contra `dev` (ou a branch-base, se for uma branch empilhada), nunca `master` direto.
---

# Criar/atualizar PR no GitHub

Use o **`gh` CLI** (`gh pr create`, `gh pr edit`, `gh pr view`). O repositório é público:
`maurofneto/GiomCaptionVideos`. Rode os comandos de dentro do clone/worktree (o `gh` descobre o
repo pelo `origin`).

## Antes de tudo

- Template do corpo: `.github/pull_request_template.md` — siga a estrutura exata (contexto,
  checklist, o que foi alterado, issues relacionadas).
- Título: `[DEV/MASTER][#número da issue] - Mensagem explicativa real`. O prefixo é a branch de
  destino (`refs/heads/dev` → `[DEV]`, `master` → `[MASTER]`).
- A branch de origem se chama `<issue>-<slug>` (ex.: `155-docs-skills-github`) — o número da
  issue sai do nome da branch. Ver `docs/GIT_WORKFLOW.md`.
- Alvo: `dev` no caso normal (é a branch padrão do repo). Se a branch é empilhada sobre outra
  ainda não mergeada, o alvo é a branch-base.
- **PR fechado/mergeado não reabre.** Se o trabalho continuou depois do merge, é um PR novo.
- Título e descrição em português (pt-BR).
- **Repo público: nunca colocar valor de segredo, token, URL com credencial ou caminho SSM com
  valor no título, descrição ou comentários.** O secret scanning bloqueia pushes, mas não texto
  de PR.

## Criar

Descrição com **newlines reais** — heredoc em arquivo, nunca `\n` literal em `--body`:

```bash
cat > /tmp/pr_body.md <<'EOF'
### Contexto
...

### O que foi alterado?
* ...

Closes #155
EOF

gh pr create --base dev --head <branch> \
  --title "[DEV][#155] - Mensagem explicativa" \
  --body-file /tmp/pr_body.md
```

Adicione `--draft` se o trabalho ainda não está completo. Termine o corpo com a linha de
atribuição do harness quando ele exigir (`🤖 Generated with [Claude Code](https://claude.com/claude-code)`).

Antes de criar, resuma o diff: `git log dev..HEAD --oneline` (no máximo os últimos 5 commits)
alimenta "O que foi alterado?".

## Atualizar

```bash
gh pr edit <n> --title "..." --body-file /tmp/pr_body.md   # título/descrição
gh pr edit <n> --base dev                                  # retarget
gh pr ready <n>                                            # tira de draft
gh pr view <n> --json state,baseRefName,mergeStateStatus
```

## Issues: sempre por palavra-chave, na descrição

Use `Closes #N` (ou `Fixes #N`) — quando o PR é mergeado na branch padrão (`dev`), a issue fecha
sozinha e o Project move o item pra **Done**. Isso substitui o "workItemIds" do Azure.

- PR contra `master` (promoção dev → master): `Closes` **não dispara** (master não é a branch
  padrão). Use `Refs #N` e feche as issues à mão, ou deixe elas já fechadas pelos PRs em `dev`.
- Vários itens: uma linha `Closes #N` por issue (`Closes #1, #2` só fecha a primeira).
- Só mencione issue que o PR realmente entrega. O número da issue é o mesmo ID que o item tinha
  no Azure Boards (#2–#153), então referências antigas continuam válidas.

## PR empilhado

Se a branch-base mescla e a branch é deletada (o repo tem "excluir branch ao mergear" ligado),
o GitHub **retargeta sozinho** os PRs empilhados para a base da base. Mesmo assim, confira com
`gh pr view <n> --json baseRefName` e, se ainda apontar pra branch antiga,
`gh pr edit <n> --base dev`.

## Regras do repo (rulesets)

`dev` e `master` exigem PR e proíbem force-push e deleção. Hoje 0 aprovações são exigidas (um
só mantenedor) e, na Fase de Actions, os status checks também serão. Nunca tentar push direto
nelas — abra PR.

## Depois de criar/atualizar

Informe o número e o link do PR. Se o merge também resolve issues que **não** estão no
`Closes`, ofereça atualizar o estado delas (skill `atualizar-board`).
