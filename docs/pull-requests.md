# Criação de Pull Requests

## Regras Gerais

- PRs são criados com o **GitHub CLI** (`gh pr create`) no repo `maurofneto/GiomCaptionVideos`.
- O título e a descrição devem ser em **português** (pt-BR).
- Sempre vincular a **issue** relacionada com `Closes #N` na descrição (uma linha por issue).
- A branch de destino **não tem valor padrão implícito** — se o usuário não informar, pergunte
  antes de criar o PR. Na prática deste repositório, quase sempre é `dev` (só vai pra `master`
  depois de aprovação humana).
- A branch de origem segue o padrão do projeto: `<número-da-issue>-descrição` (não
  `feature/`, `bugfix/`, `hotfix/`).
- **Newlines reais na descrição**: use `--body-file` (arquivo/heredoc), nunca `\n` literal em
  `--body`.
- **Repo público**: nada de segredo, token, URL com credencial ou dado pessoal no PR.

## Formato do Título

```
[DEV/MASTER][#número da issue] - Mensagem explicativa REAL sobre o que é a PR
```

Exemplos:

```
[DEV][#82] - Helicone: User=canal, Sessão reflete canal/página/vídeo/etapa
[MASTER][#83] - Limpar pipeline de deploy: variable groups dev/prod + template
```

### Regras do Título

- O prefixo entre colchetes indica a branch de destino: `DEV` ou `MASTER` (este projeto não usa
  branches `release/*`).
- O número é o da **issue** (Task/User Story/Bug), não o número do PR.
- A mensagem deve ser clara e explicar o que o PR realmente faz — não repetir o nome da branch.

## Formato da Descrição (Template)

O GitHub auto-popula [`.github/pull_request_template.md`](../.github/pull_request_template.md)
em PRs criados pela UI; ao criar via `gh`, use o mesmo texto no `--body-file`. Estrutura:

```markdown
# Pull Request

### :information_source: Título da PR
`[DEV/MASTER][#número da issue] - Mensagem explicativa REAL sobre o que é a PR`

### :speech_balloon: Contexto
(2-4 linhas: por que essa mudança existe, o que motivou)

### :hammer_and_wrench: No que eu trabalhei nessa PR?
* [ ] Testes :octopus:
* [ ] Bugfix :bug:
* [ ] Feature :new:
* [ ] Mudança estrutural :building_construction:

### :books: Issue
* [ ] Reli a issue
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

### :link: Issues relacionadas
Closes #
```

## Regras para o Agente

### Antes de Criar o PR

1. Consultar os commits da branch (`git log dev..HEAD --oneline`, no máximo os últimos 5) pra
   gerar o resumo em "O que foi alterado?".
2. Identificar a issue associada (pelo nome da branch `<n>-...` ou pelos commits).
3. Perguntar a branch de destino ao usuário se não foi informada — não assumir `dev` sem
   confirmar, exceto quando o fluxo já deixou isso implícito.
4. Prefixo do título: destino `dev` → `[DEV]`, `master` → `[MASTER]`.

### Ao Criar o PR

5. Usar `gh pr create` com:
   - `--base` / `--head`: nomes das branches (sem `refs/heads/`)
   - `--title`: seguindo `[DEV/MASTER][#ID] - Descrição`
   - `--body-file`: o template preenchido
   - `--draft` se o trabalho ainda não está completo
6. `Closes #N` fecha a issue ao mergear **só quando o destino é `dev`** (branch padrão). Em PR
   para `master` use `Refs #N`.

### Depois de Criar o PR

7. Informar o número do PR e o link pra revisão.
8. Sugerir revisores se o contexto indicar (hoje não há revisor fixo).

## Tags/Categorias Sugeridas por Tipo

| Tipo de Mudança   | Marcar no checklist                  |
|-------------------|---------------------------------------|
| Nova feature      | Feature :new:                         |
| Bug fix           | Bugfix :bug:                          |
| Refatoração/infra | Mudança estrutural :building_construction: |
| Testes            | Testes :octopus:                      |
