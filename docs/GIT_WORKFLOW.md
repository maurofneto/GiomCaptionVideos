# Git Workflow

O código e o board vivem no GitHub: <https://github.com/maurofneto/GiomCaptionVideos> (repo
público). Itens de trabalho são **Issues** organizadas no Project **"Giom — Board"**.
Ferramenta de linha de comando: `gh`.

## Branches

- **`dev`**: branch de integração e **branch padrão** do repo. Auto-deploy pra ambiente dev a
  cada merge. Todo trabalho novo parte daqui.
- **`master`**: protegido. Só recebe merge via PR — nunca push direto. Deploy de prod.
- Ambas têm ruleset: PR obrigatório, force-push e deleção bloqueados.

## Branch / worktree por tarefa

Nome padrão: **`<número-da-issue>-descrição-curta`** — só o número da issue do GitHub, sem
prefixo. Exemplo: `155-docs-skills-github`.

O número da issue é o mesmo ID que o item tinha no Azure Boards (#2–#153); itens novos
continuam a numeração. Sem issue, crie uma antes (ver skill `atualizar-board`).

Trabalho maior/experimental usa `git worktree` em vez de `checkout` direto, mantendo o
diretório principal limpo em `dev`:
```bash
git worktree add ../GiomCaptionVideos-worktrees/32-fila-ingestao-desacoplamento \
  -b 32-fila-ingestao-desacoplamento origin/dev
```

Sempre parte de `dev` atualizado (`git fetch origin dev` antes de criar a branch, ou
`git rebase origin/dev` se a branch já existir e `dev` tiver avançado).

**Branch empilhada**: quando uma Feature depende de código de outra ainda não mergeada em
`dev`, a nova branch parte da branch da feature anterior, não de `dev`, e o PR é aberto contra
essa branch. O repo apaga a branch ao mergear, e o GitHub **retargeta sozinho** os PRs
empilhados pra base da base — mesmo assim confira (`gh pr view <n> --json baseRefName`) e, se
preciso, `gh pr edit <n> --base dev`.

## Pull Requests

- Sempre contra `dev` (nunca direto em `master`, exceto a promoção dev → master).
- Título: `[DEV/MASTER][#issue] - Mensagem explicativa real`.
- Corpo segue o template em `.github/pull_request_template.md` (contexto, checklist de boas
  práticas, o que foi alterado, issues relacionadas).
- Vincule a issue com **`Closes #N`** no corpo: ao mergear em `dev` a issue fecha e o Project
  move o item pra Done. PR contra `master` usa `Refs #N` (`Closes` só vale na branch padrão).
- **PR fechado não reabre** — se surgir um ajuste depois do merge, é um PR novo (mesma branch
  de origem serve, se ainda existir).
- Criação/atualização via `gh pr create` / `gh pr edit`. Detalhes em
  [`pull-requests.md`](pull-requests.md).

## Repo público — higiene

- Nunca commitar secret real, nem em issue/PR/comentário. Secret scanning e push protection
  estão ligados no GitHub; ainda assim a responsabilidade é de quem escreve.
- Arquivos locais com credencial (`appsettings.Local.json`, `.env`, JSON de service account)
  nunca entram no repo.

## Commits

- Mensagem no imperativo, foco no "porquê" quando não é óbvio pelo diff.
- Preferir commit novo a `--amend`, exceto quando o usuário pedir explicitamente amend.
- Nunca `--no-verify` ou pular hooks sem pedido explícito.

## Ações destrutivas / infraestrutura

- `git push --force`, `reset --hard`, deletar branch: nunca sem confirmação explícita.
- Provisionamento de infraestrutura real (criar fila SQS, alterar policy IAM, rodar `ecs
  run-task` manual) segue o mesmo princípio — confirmar antes de executar, mesmo quando a
  intenção já pareceu combinada numa mensagem anterior.
