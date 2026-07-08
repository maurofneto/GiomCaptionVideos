# GiomCaptionVideos

Sistema que processa vídeos do YouTube de dois canais de igreja (**Instituto Giom** e
**Ministério Giom**) — busca legendas/transcrição, gera resumo, categoriza e indexa em
embeddings — e alimenta um bot de RAG no Telegram (**AgenteInstituto**, persona "Timóteo")
que responde perguntas de alunos com base nesse conteúdo.

Arquitetura DDD/Hexagonal em .NET 9, rodando em AWS ECS Fargate (dev/prod), com observabilidade
unificada via Sentry (traces/logs/métricas) e Helicone (proxy/sessões de LLM).

> Para detalhes de arquitetura, convenções de código e workflow de Git, ver `docs/`
> (resumo e links no fim deste arquivo).

## O que o sistema faz

1. **Pipeline de vídeos** (`Instituto.Worker` / `Ministerio.Worker`): descobre vídeos novos
   nas playlists do canal → busca legenda/transcrição → gera resumo (Claude) → categoriza
   (Instituto) → indexa embedding (Instituto, para RAG) → persiste no Supabase (Postgres +
   pgvector). Roda como ECS Task sob demanda (run-once, não é um serviço de longa duração).
2. **Bot Telegram** (`AgenteInstituto.Worker`): persona "Timóteo", responde perguntas de
   alunos buscando contexto por RAG (busca híbrida vetor + full-text) no conteúdo indexado
   pelo pipeline. Roda como ECS Service de longa duração.
3. **Observabilidade unificada**: cada execução (run → canal → playlist → página → vídeo →
   etapa) vira uma árvore de spans no Sentry; custo/tokens de cada chamada LLM vira sessão no
   Helicone; ambos compartilham o mesmo `SessaoObservabilidade`/`CorrelationId`.

## Estrutura de projetos

```
src/
  Giom.VideoManager.Domain              — entidades, value objects, interfaces (núcleo, sem dependências externas)
  Giom.VideoManager.Application         — casos de uso, pipeline (ChannelProcessor, VideoQueueConsumer), handlers
  Giom.VideoManager.Composition         — composition root: liga tudo via appsettings (Provider plugável)
  Giom.VideoManager.Infrastructure      — kernel de infra compartilhado (helpers, base)
  Giom.VideoManager.Infrastructure.*    — um adapter por dependência externa (ver tabela abaixo)
  Giom.VideoManager.Instituto.Worker    — entrypoint do worker do Instituto Giom
  Giom.VideoManager.Ministerio.Worker   — entrypoint do worker do Ministério Giom
  Giom.VideoManager.AgenteInstituto.Worker — entrypoint do bot Telegram (Timóteo)
tests/
  Giom.VideoManager.Testes.Comuns       — fakes/test doubles compartilhados
  Giom.VideoManager.*.Tests             — testes unitários por projeto (Application, Infrastructure, Anthropic, Helicone, OpenAI, Telegram)
tools/
  Giom.VideoManager.ObservabilidadeTeste — tool de validação end-to-end com vídeos fake, sem depender do YouTube real
```

### Adapters (`Infrastructure.*`) e o que cada um resolve

| Projeto | Resolve |
|---|---|
| `Infrastructure.YouTube` | Descoberta de vídeo/playlist, legenda (`YouTubeCaption`/`YtDlpCaption`/`YouTubeTranscript`), OAuth Google |
| `Infrastructure.Whisper` | Transcrição via Whisper local (fallback quando não há legenda) |
| `Infrastructure.Anthropic` | Cliente Claude (resumo, categorização, bot) |
| `Infrastructure.OpenAI` | Embeddings diretos (`DirectEmbedding`) |
| `Infrastructure.SummarizeTech` | Alternativa de resumo (provider plugável) |
| `Infrastructure.Supabase` | Persistência (Postgres + pgvector), busca híbrida |
| `Infrastructure.Mongo` | Memória de conversa do bot (alternativa) |
| `Infrastructure.Sql` | Acesso SQL genérico |
| `Infrastructure.Telegram` | Canal de chat do bot |
| `Infrastructure.Sentry` | Tracing/logs/métricas (OTel + sink customizado) |
| `Infrastructure.Helicone` | Sessões/custo/tokens de chamadas LLM |
| `Infrastructure.Sqs` | Fila de ingestão — provider AWS SQS (auto-cria a fila se não existir) |
| `Infrastructure.RabbitMq` | Fila de ingestão — provider CloudAMQP/LavinMQ (alternativa a SQS) |
| `Infrastructure.Secrets.Aws` / `.Azure` / `.HashiCorp` | Resolução de secrets — provider plugável via `Secrets:Provider` |

**Padrão-chave**: toda dependência externa vira um projeto `Infrastructure.<Nome>` próprio,
com seu próprio `ServiceCollectionExtensions.Add<Nome>(config)`. Trocar de provider é mudar
uma string no `appsettings.json` (`"Fila:Provider": "Sqs"` vs `"RabbitMq"`, por exemplo),
nunca uma mudança de código em `Application`/`Domain`.

## Como rodar localmente

Cada worker lê `appsettings.json` (padrão = prod) + `appsettings.Development.json`
(overrides de dev, ativado via `ASPNETCORE_ENVIRONMENT=Development` ou
`DOTNET_ENVIRONMENT=Development`). Secrets nunca ficam em texto no `appsettings.json` — são
placeholders em branco com comentário indicando o path real no SSM
(`/giom/{dev,prod}/{instituto,ministerio}/...`).

```bash
# Instituto (processa vídeos + fila SQS/RabbitMq conforme Fila:Provider)
cd src/Giom.VideoManager.Instituto.Worker
ASPNETCORE_ENVIRONMENT=Development dotnet run

# Ministério
cd src/Giom.VideoManager.Ministerio.Worker
ASPNETCORE_ENVIRONMENT=Development dotnet run

# Bot (long-running)
cd src/Giom.VideoManager.AgenteInstituto.Worker
ASPNETCORE_ENVIRONMENT=Development dotnet run

# Validação end-to-end sem depender do YouTube real (vídeos fake)
cd tools/Giom.VideoManager.ObservabilidadeTeste
dotnet run
```

Build/teste da solução inteira:
```bash
dotnet build GiomCaptionVideos.sln
dotnet test GiomCaptionVideos.sln
```

## Ambientes

- **dev**: branch `dev` (auto-deploy), SSM em `/giom/dev/...`, filas/recursos com sufixo `-dev`.
- **prod**: branch `master` (só via PR aprovado), SSM em `/giom/prod/...`, sem sufixo.
- AWS: conta `798229612288`, região `us-east-1`, cluster ECS `giom-cluster`. Workers do
  pipeline (`Instituto`/`Ministerio`) rodam como ECS Task sob demanda (`run-task`), não
  como Service — não há agendamento automático configurado ainda (ver `docs/ARCHITECTURE.md`
  para o estado disso). O bot (`AgenteInstituto`) roda como ECS Service de longa duração,
  um por ambiente (`giom-agenteinstituto-dev-service` / `giom-agenteinstituto-service`).

## Documentação

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — camadas DDD/Hexagonal, padrão de provider
  plugável, decorators, fila de ingestão, observabilidade unificada.
- [`docs/ENGINEERING_STANDARDS.md`](docs/ENGINEERING_STANDARDS.md) — convenções de código,
  testes, tratamento de secrets, nomenclatura.
- [`docs/GIT_WORKFLOW.md`](docs/GIT_WORKFLOW.md) — branches, worktrees, PRs, commits.
- [`CLAUDE.md`](CLAUDE.md) — regras permanentes para o Claude Code neste repo (carregado
  automaticamente toda sessão — mantido enxuto de propósito).
