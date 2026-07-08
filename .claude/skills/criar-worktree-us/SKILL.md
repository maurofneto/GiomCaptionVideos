---
name: criar-worktree-us
description: Cria uma branch e worktree novos para começar a trabalhar numa User Story, Feature ou Task do Azure Boards deste projeto (GiomCaptionVideos). Use sempre que o usuário disser algo como "vamos começar a US #XX", "cria uma branch pra essa feature", "abre um worktree pra isso", "bora trabalhar no board X", ou pedir pra iniciar trabalho em um item numerado do board — mesmo que não mencione "worktree" ou "branch" explicitamente.
---

# Criar worktree/branch de User Story

Este projeto usa `git worktree` (não `checkout` direto) pra trabalho novo, mantendo o
diretório principal sempre limpo em `dev`. Toda branch nasce a partir de `dev` atualizado —
nunca de outra branch de feature, pra evitar carregar trabalho não relacionado.

## Passos

1. **Identifique o número do work item.** Se o usuário não deu o ID do Azure Boards, pergunte
   — o nome da branch depende dele. Se ele descreveu a tarefa mas não tem o ID, pode ser
   necessário consultar o board primeiro (ver skill `atualizar-board`) ou criar o work item.

2. **Monte o slug.** Descrição curta, minúscula, com hífen, sem acento — ex: US #39/#40
   viraram `fila-ingestao-desacoplamento`. O nome final da branch é:
   ```
   GiomCaptionVideo-<id>/<id>-<slug>
   ```
   Se a tarefa cobre múltiplos work items relacionados (comum quando uma US se desdobra em
   sub-tasks inseparáveis), use o ID da User Story/Feature principal.

3. **Atualize `dev` antes de ramificar** — não presuma que o local está em dia:
   ```bash
   git fetch origin dev
   ```

4. **Crie o worktree**, num diretório irmão do repo principal (nunca dentro dele):
   ```bash
   git worktree add ../GiomCaptionVideos-worktrees/<id>-<slug> \
     -b GiomCaptionVideo-<id>/<id>-<slug> origin/dev
   ```

5. **Confirme** com `git worktree list` e informe o caminho completo pro usuário — as próximas
   ações (Read/Edit/Bash) devem usar esse novo caminho, não o diretório original.

## Por que worktree em vez de checkout

O repositório principal fica parado em `dev`, então nada impede continuar reportando
board/PR status ou rodando comandos rápidos ali enquanto o trabalho da US roda isolado no
worktree — sem stash, sem risco de misturar mudanças de duas tarefas no mesmo diretório de
trabalho.

## Se o worktree já existir

Antes de criar de novo, rode `git worktree list` — se a branch já existe (comum quando se
volta numa US depois de um tempo), só faça `git fetch origin dev` e, se `dev` avançou,
`git rebase origin/dev` dentro do worktree existente em vez de recriar do zero. Resolva
conflitos com cuidado — merges de `.sln` costumam gerar entradas duplicadas de projeto.
