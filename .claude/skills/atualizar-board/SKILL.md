---
name: atualizar-board
description: Consulta ou atualiza o status de work items no Azure Boards deste projeto (org maurofurigo, projeto Giom) — fechar/mover User Stories, Features ou Tasks depois que um PR é mergeado, ou reportar prioridades e status atual do board. Use sempre que o usuário pedir "como está o board", "fecha essas US", "atualiza o board", "qual a prioridade agora", "marca isso como feito", ou depois de confirmar que um PR foi mergeado e o trabalho relacionado deveria mudar de estado.
---

# Atualizar / consultar o Azure Boards

## Use o MCP `azure-devops` — não `az boards`

O MCP `azure-devops` (configurado em `.mcp.json`) lê e escreve UTF-8 corretamente (confirmado
com títulos cheios de acento) — nada do workaround de PowerShell + `Out-File` + leitura de
arquivo que era obrigatório com `az boards query` (que corrompe acento direto no Bash). Use:

```
mcp__azure-devops__list_work_items
  projectId: "Giom"
  wiql: "SELECT [System.Id], [System.Title], [System.WorkItemType], [System.State], [System.Parent] FROM WorkItems WHERE [System.TeamProject] = 'Giom' AND ..."
```

**Cuidado**: `list_work_items` parece devolver só um subconjunto dos campos por item (título,
estado, `assignedTo` quando existe) mesmo pedindo mais colunas no `SELECT` — se precisar de um
campo específico que não veio (ex: `System.WorkItemType`, `System.Parent`), busque o item
individual com `mcp__azure-devops__get_work_item(workItemId, expand: "fields")`.

Regra do WIQL (independe do cliente): não dá pra usar `ORDER BY` num campo não-ordenável como
`[System.Parent]` — tira o erro "field cannot be sorted by" se acontecer.

## Cache local (`.claude/cache/board.json`) — leia primeiro, sincronize sempre que escrever

Consultar o board sempre do zero (WIQL + parse) custa uma ida-e-volta e um pedaço de contexto
toda vez, mesmo pra perguntas simples tipo "qual o estado da US #82?". Por isso existe um
cache local (`.claude/cache/board.json`, fora do git — ver `.gitignore`, é dado efêmero, não
documentação).

**Isso NÃO é "confiar no JSON pra sempre, só tocar o Azure quando for atualizar".** O board
muda por fora do meu controle também (o usuário edita direto na UI, outro processo mexe) — um
cache sem validade mente com confiança. O padrão certo:

- **Leitura**: se `.claude/cache/board.json` existe e tem um `lastSynced` de menos de ~1 dia
  (ou da mesma sessão), responda a partir dele — sem consultar de novo. Se não existir, ou
  estiver mais velho que isso, ou o usuário pedir explicitamente ("confere de novo",
  "atualiza o cache"), rode a query fresca (`list_work_items`) e regrave o cache antes de
  responder.
- **Escrita**: toda vez que eu mudar o estado de um item de verdade (seção "Atualizar
  estado"), atualizo o Azure **e** o cache no mesmo passo — nunca deixo o cache desatualizado
  depois de uma escrita minha.
- Formato do cache:
  ```json
  {
    "lastSynced": "2026-07-08T09:00:00Z",
    "items": [
      {"id": 39, "title": "...", "type": "User Story", "state": "New", "parent": 32}
    ]
  }
  ```

## Consultar status/prioridade

Tipicamente filtrando por `[System.State]` (`New`, `Active`, `Resolved`, `Closed`) ou por
`[System.Parent] = <id>` pra ver a árvore de um Épico/Feature. Ao reportar pro usuário, note
explicitamente se algo parece **desatualizado** — é comum um item continuar `New` mesmo com o
trabalho já mergeado em `dev`, porque ninguém fecha o work item manualmente depois do merge.

## Atualizar estado

```
mcp__azure-devops__update_work_item
  workItemId: <id>
  state: "<Active|Resolved|Closed|...>"
```
Estados válidos variam por tipo de work item, mas `New`/`Active`/`Resolved`/`Closed` cobrem o
caso comum. **Confirme com o usuário quais IDs fechar antes de rodar em lote** — não presuma
que "US relacionada" significa todos os work items da Feature inteira; às vezes só a US
específica que foi entregue deve fechar, deixando as irmãs em aberto. Depois de cada
atualização bem-sucedida, atualize o item correspondente em `.claude/cache/board.json` (ou
apague o cache pra forçar um refresh completo na próxima leitura, se forem várias mudanças).

## Se precisar do `az boards` como fallback

O MCP cobre leitura/escrita de work item; se algo faltar nele (ex: um recurso muito específico
do board que o MCP não expõe), `az boards query`/`work-item update` continuam funcionando, mas
**voltam a ter o problema de encoding** — nesse caso, rode via PowerShell redirecionando pra
arquivo, nunca prints direto no Bash:
```powershell
$wiql = "..."
az boards query --wiql $wiql --output json | Out-File -Encoding utf8 <caminho-scratchpad>/board.json
```
Se o `az` falhar com `SSLError: CERTIFICATE_VERIFY_FAILED`, é proxy/VPN local, não um problema
do projeto — não tente "resolver" desabilitando verificação de certificado; reporte pro
usuário e sugira checar VPN/proxy.

## Depois de um merge

Sempre que um PR mergear e cobrir um work item específico, é um bom momento pra perguntar (ou
já sugerir) mover aquele item pra `Resolved`/`Closed` — não deixe isso acumular silenciosamente,
é exatamente o tipo de coisa que deixa o board mentindo sobre o estado real do projeto.
