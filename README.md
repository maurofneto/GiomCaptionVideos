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

1. **Pipeline de vídeos**: `Ingestao.Worker` descobre vídeos novos nas playlists dos dois
   canais (dado-orientado via `canal_ingestao` no Supabase, não código por canal) e publica na
   fila de Transcrição. Dali em diante, 5 workers de etapa genéricos (Transcrição/Resumo/
   Categorização/Embedding/Orquestração) processam o vídeo — legenda/transcrição → resumo
   (Claude) → categoriza → indexa embedding (RAG) → persiste no Supabase (Postgres +
   pgvector). `Ingestao.Worker` roda como ECS Task sob demanda (schedule diário via
   EventBridge); os 5 workers de etapa rodam como ECS Service de longa duração, escalando 0→N
   por profundidade de fila (Application Auto Scaling) — ver `docs/ARCHITECTURE.md`.
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
  Giom.VideoManager.Ingestao.Worker     — descobre vídeos novos (Instituto + Ministério, dado-orientado)
  Giom.VideoManager.Transcricao.Worker  — etapa: legenda/transcrição
  Giom.VideoManager.Resumo.Worker       — etapa: resumo (Claude/fallback)
  Giom.VideoManager.Categorizacao.Worker — etapa: categorização
  Giom.VideoManager.Embedding.Worker    — etapa: indexação em embeddings (RAG)
  Giom.VideoManager.Orquestracao.Worker — orquestrador central (decide próxima etapa por canal)
  Giom.VideoManager.AgenteInstituto.Worker — entrypoint do bot Telegram (Timóteo)
tests/
  Giom.VideoManager.Testes.Comuns       — fakes/test doubles compartilhados
  Giom.VideoManager.*.Tests             — testes unitários por projeto (Application, Infrastructure, Anthropic, Helicone, OpenAI, Telegram)
  Giom.VideoManager.SmokeTests          — smoke test end-to-end com vídeos fake, sem depender do YouTube real
```

### Adapters (`Infrastructure.*`) e o que cada um resolve

| Projeto | Resolve |
|---|---|
| `Infrastructure.YouTube` | Descoberta de vídeo/playlist, legenda (`YouTubeCaption`/`YtDlpCaption`/`YouTubeTranscript`), OAuth Google |
| `Infrastructure.Whisper` | Transcrição de áudio via API da OpenAI (Whisper) — último elo da cadeia |
| `Infrastructure.GrokVoice` | Transcrição de áudio via Grok Voice (xAI) — elo antes do Whisper (US #148) |
| `Infrastructure.Anthropic` | Cliente Claude (resumo, categorização, bot) |
| `Infrastructure.OpenAI` | Embeddings diretos (`OpenAiEmbedding`) |
| `Infrastructure.SummarizeTech` | Alternativa de resumo (provider plugável) |
| `Infrastructure.Supabase` | Persistência (Postgres + pgvector), busca híbrida. Banco por ambiente: prod = projeto Supabase, dev = branch `dev` do mesmo projeto — ver [`docs/runbook-ambientes-banco.md`](docs/runbook-ambientes-banco.md) |
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
# Ingestão (descobre vídeos novos, Instituto + Ministério, publica na fila de Transcrição)
cd src/Giom.VideoManager.Ingestao.Worker
ASPNETCORE_ENVIRONMENT=Development dotnet run

# Um worker de etapa (mesmo padrão pra Resumo/Categorizacao/Embedding/Orquestracao)
cd src/Giom.VideoManager.Transcricao.Worker
ASPNETCORE_ENVIRONMENT=Development dotnet run

# Bot (long-running)
cd src/Giom.VideoManager.AgenteInstituto.Worker
ASPNETCORE_ENVIRONMENT=Development dotnet run

# Smoke test end-to-end sem depender do YouTube real (vídeos fake)
cd tests/Giom.VideoManager.SmokeTests
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

### yt-dlp (Transcricao.Worker)

A imagem do `Transcricao.Worker` fixa as versões de yt-dlp, deno (runtime JS do EJS) e do plugin
de PO token `bgutil-ytdlp-pot-provider` via `ARG` no Dockerfile; o boot loga as versões. Opções
(todas desligadas por padrão, valores no SSM `/giom/{env}/pipeline/`):

| Chave | SSM | Efeito |
|---|---|---|
| `YtDlp:PoToken:Habilitado` | `/giom/{env}/pipeline/YtDlp/PoToken/Habilitado` | Liga o PO token (exige `ProviderUrl`) |
| `YtDlp:PoToken:ProviderUrl` | `/giom/{env}/pipeline/YtDlp/PoToken/ProviderUrl` | `--extractor-args youtubepot-bgutilhttp:base_url=...` |
| `YtDlp:Proxy` | `/giom/{env}/pipeline/YtDlp/Proxy` (SecureString) | `--proxy` — nunca logado, só on/off |

"Sign in to confirm you're not a bot" é classificado como `BloqueioBot` (não transitório): a
mensagem vai direto pra DLQ, sem as 6 tentativas. Detalhes e procedimento:
[`docs/runbook-ytdlp-cookies.md`](docs/runbook-ytdlp-cookies.md).

## Documentação

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — camadas DDD/Hexagonal, padrão de provider
  plugável, decorators, fila de ingestão, observabilidade unificada.
- [`docs/ENGINEERING_STANDARDS.md`](docs/ENGINEERING_STANDARDS.md) — convenções de código,
  testes, tratamento de secrets, nomenclatura.
- [`docs/GIT_WORKFLOW.md`](docs/GIT_WORKFLOW.md) — branches, worktrees, PRs, commits.
- [`CLAUDE.md`](CLAUDE.md) — regras permanentes para o Claude Code neste repo (carregado
  automaticamente toda sessão — mantido enxuto de propósito).
