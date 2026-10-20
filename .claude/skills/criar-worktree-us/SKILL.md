---
name: criar-worktree-us
description: Cria uma branch e worktree novos para começar a trabalhar numa issue (User Story, Feature, Task ou Bug) do GitHub deste projeto (GiomCaptionVideos). Use sempre que o usuário disser algo como "vamos começar a US #XX", "começa a issue 160", "cria uma branch pra essa feature", "abre um worktree pra isso", "bora trabalhar no board X", ou pedir pra iniciar trabalho em um item numerado do board — mesmo que não mencione "worktree" ou "branch" explicitamente.
---

# Criar worktree/branch de uma issue

Este projeto usa `git worktree` (não `checkout` direto) pra trabalho novo, mantendo o
diretório principal sempre limpo em `dev`. Toda branch nasce a partir de `dev` atualizado —
nunca de outra branch de feature, pra evitar carregar trabalho não relacionado (exceção: branch
empilhada, ver `docs/GIT_WORKFLOW.md`).

## Passos

1. **Identifique o número da issue.** Se o usuário não deu o número, pergunte — o nome da
   branch depende dele. Se ele descreveu a tarefa mas não tem issue, crie uma antes (ver skill
   `atualizar-board`): o número recém-criado é o que vai na branch.

2. **Monte o slug.** Descrição curta, minúscula, com hífen, sem acento — ex: `fila-ingestao-desacoplamento`.
   O nome final da branch é **só o número da issue + slug**:
   ```
   <id>-<slug>
   ```
   (não use mais o prefixo `GiomCaptionVideo-<id>/`). Se a tarefa cobre várias issues
   relacionadas, use o número da issue principal (a User Story/Feature).

3. **Atualize `dev` antes de ramificar** — não presuma que o local está em dia:
   ```bash
   git fetch origin dev
   ```

4. **Crie o worktree**, num diretório irmão do repo principal (nunca dentro dele):
   ```bash
   git worktree add ../GiomCaptionVideos-worktrees/<id>-<slug> \
     -b <id>-<slug> origin/dev
   ```
   O repo de trabalho é o clone do GitHub (`origin` = `github.com/maurofneto/GiomCaptionVideos`).

5. **Opcional — vincular a issue**: `gh issue develop <id> --name <id>-<slug>` registra a branch
   na issue; ou simplesmente use `Closes #<id>` no PR depois.

6. **Confirme** com `git worktree list` e informe o caminho completo pro usuário — as próximas
   ações (Read/Edit/Bash) devem usar esse novo caminho, não o diretório original.

## Por que worktree em vez de checkout

O repositório principal fica parado em `dev`, então nada impede continuar reportando
board/PR status ou rodando comandos rápidos ali enquanto o trabalho da issue roda isolado no
worktree — sem stash, sem risco de misturar mudanças de duas tarefas no mesmo diretório.

## Se o worktree já existir

Antes de criar de novo, rode `git worktree list` — se a branch já existe (comum quando se
volta numa issue depois de um tempo), só faça `git fetch origin dev` e, se `dev` avançou,
`git rebase origin/dev` dentro do worktree existente em vez de recriar do zero. Resolva
conflitos com cuidado — merges de `.sln` costumam gerar entradas duplicadas de projeto.
