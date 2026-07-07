# Criação de Pull Requests

## Regras Gerais

- PRs são criadas via Azure CLI (`az repos pr create`) — não há MCP do Azure DevOps configurado nesta sessão.
- O título e a descrição devem ser em **português** (pt-BR).
- Sempre vincular o **work item** relacionado (`az repos pr work-item add --id <pr> --work-items <id>`, ou `az repos pr create --work-items <id>` na criação).
- A branch de destino **não tem valor padrão** — se o usuário não informar, pergunte antes de criar o PR. Na prática deste repositório, quase sempre é `dev` (só vai pra `master` depois de aprovação humana — ver [[feedback_git_workflow]]).
- A branch de origem segue o padrão do projeto: `GiomCaptionVideo-[XXXX]/[XXXX]-descrição`, onde XXXX é o ID do work item no Board (não `feature/`, `bugfix/`, `hotfix/`).
- **CRÍTICO: usar newlines reais na descrição, nunca `\n` literal.** Ao passar a descrição via `az repos pr create --description`, usar heredoc (`$(cat <<'EOF' ... EOF)`) pra preservar quebras de linha de verdade.

## Formato do Título

```
[DEV/MASTER][Número do work item no Board] - Mensagem explicativa REAL sobre o que é a PR
```

Exemplos:

```
[DEV][82] - Helicone: User=canal, Sessão reflete canal/página/vídeo/etapa
[MASTER][83] - Limpar pipeline de deploy: variable groups dev/prod + template
```

### Regras do Título

- O prefixo entre colchetes indica a branch de destino: `DEV` ou `MASTER` (este projeto não usa branches `release/*`).
- O número é o ID do work item no Azure Boards (Task/User Story/Bug), não o número do PR.
- A mensagem deve ser clara e explicar o que a PR realmente faz — não repetir o nome da branch.

## Formato da Descrição (Template Obrigatório)

A descrição segue o template em [`.azuredevops/pull_request_template.md`](../.azuredevops/pull_request_template.md) — o Azure DevOps já auto-popula esse conteúdo em PRs criadas pela UI; ao criar via CLI, usar o mesmo texto como `--description`. Estrutura:

```markdown
# Pull Request

### :information_source: Título da PR
`[DEV/MASTER][Número do work item] - Mensagem explicativa REAL sobre o que é a PR`

### :speech_balloon: Contexto
(2-4 linhas: por que essa mudança existe, o que motivou)

### :hammer_and_wrench: No que eu trabalhei nessa PR?
* [ ] Testes :octopus:
* [ ] Bugfix :bug:
* [ ] Feature :new:
* [ ] Mudança estrutural :building_construction:

### :books: História
* [ ] Reli a história/task
* [ ] Atendi a todos os critérios de aceite

### :white_check_mark: Melhores práticas
* [ ] Segui as boas práticas (DDD/hexagonal — ver CLAUDE.md)
* [ ] Resolvi os erros de build/lint

### :octopus: Testes unitários
* [ ] Fiz os testes básicos
* [ ] Nenhum teste antigo quebrou (dotnet test)

### :memo: O que foi alterado?
* Item
* Item
```

## Regras para o Agente

### Antes de Criar o PR

1. Consultar os commits da branch (`git log dev..HEAD --oneline`, no máximo os últimos 5) pra gerar o resumo em "O que foi alterado?".
2. Identificar o work item associado (pelo nome da branch `GiomCaptionVideo-XXXX/...` ou pelos commits).
3. Perguntar a branch de destino ao usuário se não foi informada explicitamente — não assumir `dev` por padrão sem confirmar, exceto quando o próprio fluxo de trabalho já deixou isso implícito (ex.: branch criada a partir de `dev` especificamente pra esse fim).
4. Determinar o prefixo do título a partir da branch de destino: `refs/heads/dev` → `[DEV]`, `refs/heads/master` → `[MASTER]`.

### Ao Criar o PR

5. Usar `az repos pr create` com:
   - `--repository`: nome do repositório (`GiomCaptionVideos`)
   - `--title`: seguindo `[DEV/MASTER][ID] - Descrição`
   - `--description`: o template completo preenchido (via heredoc, newlines reais)
   - `--source-branch` / `--target-branch`: nomes das branches (sem `refs/heads/` — o CLI aceita o nome puro)
   - `--work-items`: ID(s) do work item vinculado
   - `--draft true` se o trabalho ainda não está completo

### Depois de Criar o PR

6. Informar o ID do PR e o link pra revisão.
7. Sugerir revisores se o contexto indicar (ex.: mudança de infra/pipeline → nenhum revisor fixo hoje, mas seria o caso de sinalizar).

## Tags/Categorias Sugeridas por Tipo

| Tipo de Mudança   | Marcar no checklist                  |
|-------------------|---------------------------------------|
| Nova feature      | Feature :new:                         |
| Bug fix           | Bugfix :bug:                          |
| Refatoração/infra | Mudança estrutural :building_construction: |
| Testes            | Testes :octopus:                      |
