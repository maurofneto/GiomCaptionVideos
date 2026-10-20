# CLAUDE.md

Instruções permanentes para o Claude Code neste repositório. Este arquivo é carregado em
**toda sessão automaticamente** — fica enxuto de propósito. Detalhe fica nos docs linkados
abaixo (só custam tokens quando efetivamente lidos).

## Projeto

Sistema DDD/Hexagonal (.NET 9) que processa vídeos do YouTube (Instituto Giom / Ministério
Giom) e alimenta um bot RAG no Telegram. Visão geral completa: [`README.md`](README.md).

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — camadas, provider plugável, decorators,
  fila de ingestão, observabilidade unificada (Sentry + Helicone).
- [`docs/ENGINEERING_STANDARDS.md`](docs/ENGINEERING_STANDARDS.md) — convenções de código,
  testes, secrets, AWS CLI no Windows.
- [`docs/GIT_WORKFLOW.md`](docs/GIT_WORKFLOW.md) — branches, worktrees, PRs, commits.

## Regras que sempre valem (sem precisar ler os docs)

- Código e board vivem no GitHub (`maurofneto/GiomCaptionVideos`, repo **público**); usar o
  `gh` CLI. Issues = work items; Project "Giom — Board" (nº 8).
- `dev` = integração (auto-deploy, branch padrão); `master` só via PR, nunca push direto.
- Branch/worktree nova: `[XXXX]-descrição` (XXXX = número da issue), a partir de `dev`
  atualizado. PR com `Closes #XXXX`.
- Repo público: nunca colocar secret/token/dado pessoal em código, issue, PR ou comentário.
- Nunca commitar secret real — placeholder em branco + comentário do path SSM
  (`/giom/{dev,prod}/{instituto,ministerio}/...`).
- Decorator sobre a interface do Domain do meio pra integrar dois adapters — nunca referência
  direta entre `Infrastructure.*` projetos.
- Ações de infraestrutura real (provisionar fila, alterar IAM, rodar task manual) sempre
  confirmadas antes de executar.

## Economia de tokens/créditos

- **Nunca despejar JSON/log cru grande na saída.** Ao consultar AWS CLI (logs, boards, describe-*), filtrar no servidor com `--query` em vez de trazer tudo e filtrar depois. Se o output for grande mesmo assim, redirecionar pra arquivo e ler só o trecho relevante (`Read` com `offset`/`limit`, ou `Grep` com `head_limit`), nunca colar o arquivo inteiro na resposta.
- **Evitar `/fast` sem pedido explícito do usuário.** Fast mode usa Opus com pricing mais alto por velocidade — não é um modo econômico. Sonnet é o padrão; só escalar pra Opus em problemas de raciocínio genuinamente difíceis.
- **Rebuilds/testes escopados.** Pra mudanças isoladas, rodar só o projeto de teste afetado (`dotnet test tests/X.Tests`) em vez da solução inteira. Rodar a solução completa só quando a mudança tocar múltiplos projetos ou antes de um PR.
- **Delegar leitura de artefatos grandes pra subagent com `model: haiku`** quando a tarefa for só puxar/filtrar informação (logs da ECS, listas grandes de issues, dumps de API) sem precisar de raciocínio pesado — mantém esse volume fora do contexto principal.
- **`/compact` em pontos de corte naturais entre tarefas**, não no meio de uma tarefa em andamento (invalida o cache da conversa).
- **Preferir sessão nova pra tarefas não relacionadas** em vez de continuar uma sessão longa e heterogênea — a memória automática (`memory/`) já preserva o contexto relevante entre sessões, então trocar de sessão não perde informação importante.
