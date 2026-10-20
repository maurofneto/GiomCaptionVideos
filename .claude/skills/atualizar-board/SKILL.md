---
name: atualizar-board
description: Consulta ou atualiza o board deste projeto no GitHub (Issues + Project "Giom — Board", maurofneto/GiomCaptionVideos) — fechar/mover User Stories, Features ou Tasks depois que um PR é mergeado, criar issues, ou reportar prioridades e status atual. Use sempre que o usuário pedir "como está o board", "fecha essas US", "atualiza o board", "qual a prioridade agora", "marca isso como feito", "cria uma issue", ou depois de confirmar que um PR foi mergeado e o trabalho relacionado deveria mudar de estado.
---

# Atualizar / consultar o board (GitHub Issues + Project)

O board vive em **GitHub Issues** (repo `maurofneto/GiomCaptionVideos`) organizadas no **Project
v2 nº 8 "Giom — Board"** (dono `maurofneto`). Use o `gh` CLI. O número da issue é o mesmo ID que
o item tinha no Azure Boards (#2–#153); itens novos continuam a numeração.

Modelo:

| Conceito Azure | No GitHub |
|---|---|
| Epic / Feature / User Story / Task / Bug | label `epic`/`feature`/`user-story`/`task`/`bug` **e** campo **Tipo** do Project |
| Estado New/Active/Closed | campo **Status** do Project: Todo / In Progress / Done (e a issue aberta/fechada) |
| Parent | **sub-issues** (hierarquia Epic > Feature > US > Task) |
| Tags (TimoteoIA, RAG…) | labels |
| Prioridade (tag) | campo **Prioridade**: Alta/Média/Baixa |
| Severity (bugs) | campo **Severidade** |

Views do Project: Tabela, Quadro (por Status), Bugs abertos, Hierarquia.

## Consultar

Sempre filtre no servidor com `--json` + `-q` (não despeje listas enormes):

```bash
gh issue list -R maurofneto/GiomCaptionVideos --state open --limit 100 \
  --json number,title,labels -q '.[]|"#\(.number) \(.title)"'

gh issue list -R maurofneto/GiomCaptionVideos --label bug --state open
gh issue view 137 -R maurofneto/GiomCaptionVideos --json title,state,body,labels
```

Itens do Project com campos (Status/Tipo/Prioridade):

```bash
gh project item-list 8 --owner maurofneto --limit 300 --format json \
  -q '.items[]|select(.status!="Done")|[.content.number,.status,.tipo,.title]|@tsv'
```

Árvore de um Épico/Feature (sub-issues):

```bash
gh api repos/maurofneto/GiomCaptionVideos/issues/<n>/sub_issues -q '.[]|"#\(.number) \(.state) \(.title)"'
```

Não há mais cache local (`.claude/cache/board.json`) — a consulta é barata e sempre fresca.
Ao reportar, avise quando algo parecer **desatualizado**: é comum uma issue ficar aberta mesmo
com o trabalho já mergeado em `dev`.

## Criar issue

```bash
gh issue create -R maurofneto/GiomCaptionVideos --title "..." --body-file /tmp/issue.md \
  --label user-story --label TimoteoIA
```

Depois adicione ao Project e defina o **Tipo** (o Status nasce como Todo):

```bash
gh project item-add 8 --owner maurofneto --url <url-da-issue>
```

Para um filho, `gh api -X POST repos/maurofneto/GiomCaptionVideos/issues/<pai>/sub_issues -F sub_issue_id=<id-numérico-da-issue>`
(o `id` numérico vem de `gh api repos/.../issues/<n> -q .id`, não é o número da issue).

## Atualizar estado

- **Fechar**: `gh issue close <n> --reason completed` (ou `--reason "not planned"`). O Project
  move o item para Done sozinho (workflow "Item closed"). É o caso normal — a maioria sai por
  `Closes #N` no PR (ver skill `criar-pr-template`).
- **Reabrir**: `gh issue reopen <n>`.
- **Marcar In Progress** (campo do Project, não existe na issue): pegue os IDs uma vez e edite:

```bash
gh project field-list 8 --owner maurofneto --format json   # ids de Status e das opções
gh project item-list 8 --owner maurofneto --limit 300 --format json -q '.items[]|select(.content.number==<n>)|.id'
gh project item-edit --id <item-id> --project-id <project-id> \
  --field-id <status-field-id> --single-select-option-id <in-progress-option-id>
```

`<project-id>` vem de `gh project view 8 --owner maurofneto --format json -q .id`. Ao colar
IDs de saída gerada no Windows, remova `\r` (`tr -d '\r'`).

**Confirme com o usuário quais issues fechar antes de rodar em lote** — não presuma que "US
relacionada" significa tudo da Feature inteira; às vezes só a US entregue deve fechar.

## Depois de um merge

Quando um PR mergear e cobrir uma issue específica, normalmente o `Closes #N` já fechou. Se o PR
não tinha a palavra-chave (ou era contra `master`), ofereça fechar a issue — não deixe isso
acumular, é o que faz o board mentir sobre o estado real do projeto.

## Repo público

Issues e comentários são públicos. Nunca colocar valor de segredo, token ou dado pessoal; para
rotação de credenciais use só nomes de provedores (ver issue #154).
