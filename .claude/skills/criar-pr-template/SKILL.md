---
name: criar-pr-template
description: Cria ou atualiza um Pull Request no Azure Repos deste projeto seguindo o template padrão do time. Use sempre que o usuário pedir pra "mandar o PR", "abrir PR", "atualizar a descrição do PR", "criar pull request", "sobe isso aí" depois de um trabalho pronto, ou quando uma branch estiver pronta pra revisão — sempre contra `dev` (ou a branch-base, se for uma branch empilhada), nunca `master` direto.
---

# Criar/atualizar PR no Azure Repos

## Antes de tudo

- Template do corpo: `.azuredevops/pull_request_template.md` — siga a estrutura exata
  (contexto, checklist, o que foi alterado).
- Título: `[DEV/MASTER][Número do work item] - Mensagem explicativa real`.
- Alvo: `dev` no caso normal. Se a branch é empilhada sobre outra feature ainda não mergeada
  (ver `docs/GIT_WORKFLOW.md`), o alvo é a branch-base, não `dev`.
- **PR fechado não reabre.** Se o trabalho continuou depois que um PR já foi mergeado, é
  sempre um PR novo — não tente reaproveitar o número antigo.

## Criar

```bash
az repos pr create \
  --title "[DEV][<id>] - <mensagem>" \
  --description "<corpo do template preenchido>" \
  --source-branch "<branch-atual>" \
  --target-branch "dev" \
  --work-items <id1> <id2>
```

## Atualizar descrição — cuidado com acentuação

`az repos pr update --description` **corrompe acentos** quando rodado direto (tanto via Bash
quanto via `Invoke-RestMethod` do PowerShell sem cuidado extra) — o pipeline de encoding entre
o terminal do Windows e a API do Azure DevOps perde caracteres fora de ASCII. O jeito que
funciona de verdade é PowerShell com `HttpClient`/`StringContent` UTF-8 explícito, chamando a
REST API direto:

```powershell
Add-Type -AssemblyName System.Net.Http
$token = az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv
$uri = "https://<org>.visualstudio.com/<projectGuid>/_apis/git/repositories/<repoGuid>/pullRequests/<id>?api-version=7.1"
$json = @{ description = $corpoCompleto } | ConvertTo-Json -Compress
$client = New-Object System.Net.Http.HttpClient
$client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $token)
$request = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod("PATCH")), $uri)
$request.Content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, "application/json")
$client.SendAsync($request).GetAwaiter().GetResult()
```
(project/repo GUID: pegue de `az repos pr show --id <id>` — vem no campo `repository.project.id`
e `repository.id`.)

## Gotcha da API: descrição terminando em referência de work item

A API do Azure DevOps **rejeita** (`400 Invalid argument value`) uma descrição que termina
**exatamente** numa menção tipo `#40` sem nada depois — o parser de menção deles quebra nesse
caso específico de fim de string. Sempre feche a descrição com uma quebra de linha extra
depois da linha `Related work items: #X, #Y`. Se a atualização falhar com esse erro genérico e
a descrição parecer válida, essa é a primeira coisa a checar.

## Depois de criar/atualizar

Se o merge do PR também resolve work items do board, lembrar de oferecer atualizar o estado
deles (ver skill `atualizar-board`) — não é automático.
