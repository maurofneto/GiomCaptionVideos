---
name: criar-pr-template
description: Cria ou atualiza um Pull Request no Azure Repos deste projeto seguindo o template padrão do time. Use sempre que o usuário pedir pra "mandar o PR", "abrir PR", "atualizar a descrição do PR", "criar pull request", "sobe isso aí" depois de um trabalho pronto, "retargeta o PR", ou quando uma branch estiver pronta pra revisão — sempre contra `dev` (ou a branch-base, se for uma branch empilhada), nunca `master` direto.
---

# Criar/atualizar PR no Azure Repos

## Use o MCP `azure-devops` — não `az repos pr`

O MCP `azure-devops` (configurado em `.mcp.json`) é o caminho certo pra criar/ler/atualizar
PR: `mcp__azure-devops__create_pull_request`, `get_pull_request`, `update_pull_request`. Ele
lê e escreve acentuação UTF-8 corretamente (confirmado testando descrição cheia de acento) —
o `az repos pr update`/`Invoke-RestMethod` direto **corrompiam** acento, era por isso que a
gente tinha que passar por PowerShell + `HttpClient` na mão antes. Não precisa mais disso pra
título/descrição/reviewers/work items.

## Antes de tudo

- Template do corpo: `.azuredevops/pull_request_template.md` — siga a estrutura exata
  (contexto, checklist, o que foi alterado).
- Título: `[DEV/MASTER][Número do work item] - Mensagem explicativa real`.
- Alvo: `dev` no caso normal. Se a branch é empilhada sobre outra feature ainda não mergeada
  (ver `docs/GIT_WORKFLOW.md`), o alvo é a branch-base.
- **PR fechado não reabre.** Se o trabalho continuou depois que um PR já foi mergeado, é
  sempre um PR novo — não tente reaproveitar o número antigo.

## Criar / atualizar (via MCP)

```
mcp__azure-devops__create_pull_request
  repositoryId: "GiomCaptionVideos"
  projectId: "Giom"
  sourceBranch / targetBranch, title, description, workItemIds
```
```
mcp__azure-devops__update_pull_request
  repositoryId: "GiomCaptionVideos"
  pullRequestId: <id>
  projectId: "Giom"
  description / title / status / addWorkItemIds / addReviewers ...
```

## Work items: só pelo relacionamento, nunca por texto na descrição

`workItemIds` (create) / `addWorkItemIds` (update) já criam o link real do PR com o work item
— aparece na aba "Work items" do PR e no work item também. **Não escrever uma linha tipo
"Related work items: #X, #Y" na descrição** — isso é texto redundante que só consome espaço
(a descrição tem limite de 4000 caracteres, ver abaixo) sem adicionar nada que o relacionamento
já não mostre. Linkar sempre via `addWorkItemIds`/`workItemIds`, nunca por menção em texto.

## PR empilhado: "retarget automático" NÃO é automático de verdade

O `docs/GIT_WORKFLOW.md` descrevia isso como algo que acontece sozinho quando a PR-base
mescla — **não acontece**. Confirmado na prática: PR #13 (empilhado sobre a branch da
Feature #33) continuou apontando pra essa branch muito depois dela já ter mergeado em `dev`,
até alguém checar e corrigir manualmente. **Sempre que uma branch-base mesclar, confira as
PRs empilhadas nela (`get_pull_request` no `targetRefName`) e retargete se ainda não mudou.**

`update_pull_request` (MCP) **não tem campo pra mudar `targetRefName`** — nem o `az repos pr
update` tem essa flag. Pra retarget, só via REST API direta:

```powershell
Add-Type -AssemblyName System.Net.Http
$token = az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv
$uri = "https://<org>.visualstudio.com/<projectGuid>/_apis/git/repositories/<repoGuid>/pullRequests/<id>?api-version=7.1"
$json = @{ targetRefName = "refs/heads/dev" } | ConvertTo-Json -Compress
$client = New-Object System.Net.Http.HttpClient
$client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $token)
$request = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod("PATCH")), $uri)
$request.Content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, "application/json")
$client.SendAsync($request).GetAwaiter().GetResult()
```
(project/repo GUID: pegue de `mcp__azure-devops__get_pull_request` — vem em `repository.project.id`
e `repository.id`. `targetRefName`/`sourceRefName` só aceitam esse formato, não use
`update_pull_request` do MCP pra isso.)

## Gotcha da API: descrição terminando em referência de work item

A API do Azure DevOps **rejeita** (`400 Invalid argument value`) uma descrição que termina
**exatamente** numa menção tipo `#40` sem nada depois — o parser de menção deles quebra nesse
caso específico de fim de string, independente de qual cliente (MCP, REST, CLI) você usa pra
escrever. Se a descrição terminar com esse tipo de menção por qualquer motivo, feche com uma
quebra de linha extra depois dela. Se a atualização falhar com esse erro genérico e a descrição
parecer válida, essa é a primeira coisa a checar.

## Limite de 4000 caracteres na descrição

A API rejeita (`Invalid argument value`) descrição com mais de 4000 caracteres. Textos como a
lista de work items relacionados (ver seção acima — usar o relacionamento, não texto) já não
competem por esse espaço; mesmo assim, PRs com "O que foi alterado" extenso podem estourar —
priorizar o essencial (o que mudou e por quê) e cortar detalhe secundário antes de tentar de
novo.

## Depois de criar/atualizar

Se o merge do PR também resolve work items do board, lembrar de oferecer atualizar o estado
deles (ver skill `atualizar-board`) — não é automático.
