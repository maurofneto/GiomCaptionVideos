# Arquitetura

## Camadas (DDD / Hexagonal)

```
Domain          → entidades, value objects, interfaces (portas). Zero dependência externa.
Application     → casos de uso / orquestração (ChannelProcessor, VideoQueueConsumer, handlers).
                  Depende só de Domain (via interfaces).
Infrastructure.*→ adapters. Cada dependência externa (YouTube, Anthropic, Sentry, SQS...) é
                  um projeto próprio, implementando interfaces do Domain.
Composition     → composition root. Único lugar que conhece TODOS os adapters e decide, via
                  appsettings, qual concretização usar pra cada interface.
Worker/Bot      → entrypoint (Program.cs) fino: monta o Host, chama Composition, roda.
```

Regra de dependência: **Domain não conhece nada; Application só conhece Domain; Infrastructure
implementa Domain; Composition conhece tudo.** Nunca o inverso.

## Padrão de provider plugável

Toda decisão de "qual implementação usar" vive no `appsettings.json`, nunca em código:

```json
"Fila": { "Provider": "Sqs" },          // ou "RabbitMq"
"Transcription": { "Provider": "YouTubeCaption" },  // ou "YtDlpCaption", "YouTubeTranscript"
"Summary": { "Provider": "Claude" },
"Embedding": { "Provider": "Direct" },
"Secrets": { "Provider": "AwsParameterStore" }      // ou Azure, HashiCorp
```

Cada `Infrastructure.<Nome>` expõe `AddXxx(IServiceCollection, IConfiguration)` em seu próprio
`ServiceCollectionExtensions`. `Composition` lê o `Provider` e chama o `Add*` certo — trocar
de fila (SQS → RabbitMq) ou de resolvedor de secret é mudar uma string, nunca tocar em
`Application`.

## Comunicação entre adapters — regra de camadas

**Dois adapters nunca se conhecem diretamente.** Se A (ex: `Infrastructure.Sqs`) precisa
disparar algo que depende de B (ex: `Infrastructure.Sentry`), a integração vira um **decorator
sobre a interface do Domain que fica no meio** — nunca uma referência direta de projeto entre
os dois adapters. Exemplo real: `TelemetriaCaptionService` decora `ICaptionService`
adicionando tracing/custo, sem o adapter de legenda (`YouTubeCaptionService`) saber que
telemetria existe.

## Pipeline por etapa (Feature #33 — `IFilaPublisher<T>` / `IFilaConsumer<T>`)

Abstração genérica no Domain, implementada por `Infrastructure.Sqs` (AWS SQS — cria a fila
sozinha se não existir, via `SqsFilaCanal.ResolverQueueUrlAsync`, idempotente) e
`Infrastructure.RabbitMq` (CloudAMQP/LavinMQ, ambos falam AMQP 0-9-1).

**Transcrição não é caso especial — é só mais uma etapa.** Existem 4 filas independentes
(`transcricao`, `resumo`, `categorizacao`, `embedding`), todas carregando o mesmo tipo de
mensagem — `VideoEtapaMensagem(Video, SessaoObservabilidade?, TraceParent)` — e todas
consumidas pelo mesmo `EtapaQueueConsumer` genérico (reaplica sessão/trace, chama o
delegate da etapa, relança erro pro retry).

### Orquestração central (Feature #104) — event-driven orquestrado, não mais choreografado

Até a Feature #104, cada etapa **decidia sozinha** o que publicar em seguida (choreografia —
a lógica de sequenciamento ficava espalhada, duplicada entre `InstitutoPipelineHandler` e
`MinisterioPipelineHandler`, um handler por canal implementando todas as interfaces de etapa
que fazia sentido pro seu `ProcessingOptions`). Isso também significava rodar **2 execuções
separadas** (uma por canal) pra fazer, na prática, o mesmo tipo de trabalho.

Desde a #104: cada worker de etapa (`Transcricao.Worker`, `Resumo.Worker`,
`Categorizacao.Worker`, `Embedding.Worker`) só **recebe, processa e avisa que terminou** —
publica `EtapaConcluidaMensagem(Video, Etapa, Sessao, TraceParent)` numa fila única de eventos
(`giom-videos-etapa-concluida-{env}`) e não sabe nada sobre canal nem sobre o que vem depois.
Quem decide é o **`PipelineHandler`** (orquestrador central, único — não mais duplicado por
canal), hospedado no novo `Orquestracao.Worker`: escuta a fila de eventos e resolve a
topologia por canal (`TopologiaOptions.Topologia[canal][etapa]` → lista de próximas etapas;
lista vazia = terminal, marca `ProcessedAt`).

```
video.transcricao ──> [processa] ──> etapa-concluida("transcricao")
                                            │
                                            ▼
                                     PipelineHandler
                              (só ele sabe: Instituto → [resumo, embedding]
                                            Ministério → [resumo])
                                            │
                              ┌─────────────┴─────────────┐
                              ▼                            ▼
                       video.resumo                 video.embedding
```

As 4 filas de etapa passam a ser **únicas, compartilhadas entre os canais** (não mais uma por
canal) — `Video.ChannelId` já identifica de quem é o vídeo, e o `PipelineHandler` é o único
lugar que precisa saber disso. Isso elimina a duplicação de execução (1 worker por etapa no
total, não 1 por canal × etapa) sem misturar secrets/billing entre canais nos providers de IA
(a única credencial que de fato divergia entre Instituto/Ministério — `ClaudeResumo` — foi
unificada; as demais já eram o mesmo valor ou nem existiam pro Ministério).

**Ingestão** — desde a US #105, um único `Ingestao.Worker` cobre todos os canais via
`CanalIngestaoVideoHandler` (dado-orientado: `IsRelevant` resolvido por `CanalIngestaoConfig.
RelevanciaTipo` lido do Supabase, não por código por canal). Os projetos antigos
`Instituto.Worker`/`Ministerio.Worker` (com `InstitutoPipelineHandler`/`MinisterioPipelineHandler`,
que faziam a mesma coisa só que hard-coded por canal) e o processamento monolítico ainda mais
antigo (`InstitutoVideoHandler`/`MinisterioVideoHandler`/`IVideoProcessador`, legenda→resumo→
categorização→embedding numa chamada só, já morto/não registrado em DI havia tempo) foram
**removidos do repositório em 2026-07-16**, depois de confirmar que o `Ingestao.Worker` já
cobria os dois canais em prod (schedule único `giom-ingestao-daily` substituindo
`giom-instituto-daily`/`giom-ministerio-daily`).

**Transcrição — credenciais por canal (US #146)** — o `Transcricao.Worker` espelha a Ingestão
no bootstrap OAuth: `AddTranscricaoMultiCanal` lê `canal_ingestao`, registra `YouTubeService`
keyed por `ChannelId` e escreve cookies yt-dlp Netscape por canal a partir de
`{secrets_key}:YtDlp:CookiesContent` (SSM `/giom/{env}/canais/{instituto|ministerio}/`).
`YouTubeCaptionService` / `YtDlpCaptionService` / Whisper resolvem a credencial/cookie pelo
`Video.ChannelId`. O worker carrega dois prefixos SSM (`Secrets:Aws:Paths`: `pipeline/` +
`canais/`). `pipeline/Google/*` **não** é fonte de credencial: só `{secrets_key}:Google:*`
(`ClientId` por canal opcional, senão `Google:ClientId` compartilhado). Credencial inválida/ausente
de um canal pula só esse canal (`FalhasCredencialPorCanal` guarda o motivo) e
`YouTubeCaptionService` lança `CredencialCanalIndisponivelException` pra cadeia de fallback
seguir; nunca há login interativo. Fallback legado: `pipeline/YtDlp/CookiesContent` (Bug #144)
até os cookies por canal existirem. Runbook de rotação: [`docs/runbook-ytdlp-cookies.md`](runbook-ytdlp-cookies.md).

**Client OAuth da credencial por canal** — o client tem de ser **Web application**, o mesmo da
página de reconexão (`web/reconexao`, PR-03), que é quem emite o refresh token. O fluxo
Desktop/loopback (`GoogleWebAuthorizationBroker` + `LocalServerCodeReceiver`) era o fluxo antigo
e não é mais suportado. O refresh token fica **amarrado ao client que o emitiu**: trocar o
ClientId exige reconectar os dois canais pela página e gravar o ClientSecret do client Web em
`/giom/{env}/canais/{secrets_key}/Google/ClientSecret`. O boot loga `clientId=` por canal
(identificador público; `projeto=` sozinho não distingue Desktop de Web, que compartilham o
número do projeto) para conferir com o ClientId da página. Refresh recusado
(`invalid_grant`/`unauthorized_client`) é registrado com a dica de token revogado/expirado ou
emitido por outro client.

**Topologia de execução**: cada worker de etapa é 1 processo, 1 consumer só (não mais
`Task.WhenAll` de várias etapas no mesmo processo). Migração 2026-07
(`infra/ecs-services-etapas/`): deixou de ser `run-task` sob demanda (disparado por uma Lambda
watchman reagindo a poll de SQS) e virou **ECS Service de longa duração + Application Auto
Scaling 0→N por profundidade de fila** — ver seção "Ambientes AWS" abaixo pro motivo da
mudança. O fan-out (Resumo/Embedding começarem assim que o primeiro vídeo é transcrito)
continua acontecendo, só que via mensagens passando pelo `PipelineHandler` em vez de uma
chamada direta dentro do mesmo processo. O `SqsFilaConsumer` (drena a fila e o processo termina
sozinho) agora tem um grace period de idle (`SqsOptions.IdleGracePeriodSeconds`, default 3min)
antes de encerrar — evita que o Service tenha que provisionar outra task Fargate do zero só
porque o poll anterior achou a fila vazia por um instante.

### DLQ e retry (Feature #34)

`SqsFilaCanal` garante a fila principal **e** uma DLQ irmã (`{fila}-dlq`, retenção 14 dias),
com `RedrivePolicy` (`maxReceiveCount`) apontando uma pra outra — depois de N tentativas sem
confirmação, o próprio SQS move a mensagem pra DLQ. Filas criadas antes dessa policy existir
são convergidas via `SetQueueAttributes` (não recriadas).

**Retry é dinâmico pela exceção**, não um delay fixo: `SqsFilaConsumer` calcula o tempo de
`ChangeMessageVisibility` olhando o tipo de erro. Falha transitória comum usa um delay curto
(retry ainda na mesma run, dentro da janela do long poll). Rate limit — `RateLimitExcedidoException`
(Domain, `Exceptions/`) ou `HttpRequestException` com status 429 — usa um delay bem maior
(ou o `Retry-After` que o provedor informou), porque cota estourada não é "tenta de novo em
10 segundos que resolve": é fato consumado até o provedor liberar de novo. Cada adapter (
`ResilientYouTubeApiService`, `ClaudeResumoService`/`ClaudeCategorizacaoService`,
`OpenAIEmbeddingService`) mapeia o formato de erro do SEU provedor pra
`RateLimitExcedidoException`, sem a fila conhecer nada de YouTube/Claude/OpenAI. Na última
tentativa, loga `Error` (vira alerta no Sentry pelo sink já existente) e zera a visibilidade
pra mover pra DLQ ainda nesta run.

**Circuit breaker por quota (YouTube)**: `ResilientYouTubeApiService` abre o circuito já na
**primeira** ocorrência de `quotaExceeded` (não espera acumular falhas numa janela — quota
estourada não é intermitente, é fato até o reset diário), com `BreakDuration` de 1h em vez de
minutos. `BrokenCircuitException` também vira `RateLimitExcedidoException`.

## Propagação de sessão/trace pela fila

`Activity.Current` do .NET se perde entre publish e consume (a run inteira do publisher já
terminou e fechou seus spans antes do consumer rodar). Por isso `VideoParaProcessar` carrega
o `SessaoObservabilidade` completo e o `traceparent` (W3C) capturado no momento do publish
(`ITracingService.CapturarContextoAtual()`). O consumer reata a árvore de spans passando esse
contexto pro `ActivitySource.StartActivity(..., parentContext)` e restaura a sessão via
`ISessionContext.Definir(...)` — sem isso, cada vídeo consumido viraria uma trace raiz isolada
no Sentry, quebrando a árvore `pipeline.run > canal > playlist > página > vídeo > etapas`.

## Observabilidade unificada (Sentry + Helicone)

- **Sentry**: tracing (spans aninhados), logs, métricas custom (`giom.tempo_ms`,
  `giom.custo_usd`, `giom.tokens_entrada`, `giom.tokens_saida`, tagueadas por `tipo`). Sink
  customizado no Serilog + `TracerProvider` do OpenTelemetry — a ordem de inicialização
  importa (`TracerProvider` antes de `SentrySdk.Init`), ver `Infrastructure.Sentry`.
- **Helicone**: proxy do Anthropic — sessões (`User` = canal, `Session` = canal/página/
  vídeo/etapa), custo e tokens por chamada.
- Os dois compartilham `SessaoObservabilidade` (mesmo `CorrelationId`/`SessionPath`) — uma
  chamada de API aparece correlacionada nos dois sistemas sem esforço extra por chamador.
- Dashboards existentes no Sentry: "Giom - Observabilidade" (latência/custo/tokens do bot e da
  pipeline), "Giom - Erros" (erros por ambiente), "Giom - Saúde" (vídeos processados/falhos,
  mensagens do bot, erros, gasto e runs da pipeline — visão de saúde geral em 24h).

## Secrets

Nunca em texto no `appsettings.json` versionado. Placeholder em branco + comentário indicando
o path real:
```json
"ApiKey": "", // secret — valor real só no SSM (/giom/prod/instituto/ClaudeResumo/ApiKey)
```
Resolvido em runtime via `Secrets:Provider` (AWS Parameter Store é o usado hoje;
Azure/HashiCorp existem como adapters alternativos). Path por ambiente:
`/giom/{dev,prod}/{instituto,ministerio}/...`.

## Ambientes AWS

Conta `798229612288`, região `us-east-1`, cluster ECS `giom-cluster`. `Ingestao.Worker` roda
como ECS Task sob demanda (`aws ecs run-task`), disparada pelo EventBridge Schedule
`giom-ingestao-daily`/`giom-ingestao-dev-daily` (cron diário) — cobre Instituto e Ministério
juntos via `canal_ingestao`. `AgenteInstituto.Worker` roda como ECS Service de longa duração
(um por ambiente).

Os 5 workers de etapa (Transcrição/Resumo/Categorização/Embedding/Orquestração) também rodam
como **ECS Service de longa duração**, um por (etapa, ambiente) — ver
`infra/ecs-services-etapas/README.md` pra topologia completa, scripts de provisionamento e o
desenho do autoscaling. Migração 2026-07: o gatilho anterior (Lambda `giom-fila-watchman`/
`-dev` reagindo a poll de SQS com `ecs.run_task`) tinha uma corrida estrutural com o modelo
run-to-completion dos workers — bug real ao vivo em 2026-07-15, mensagens redrivadas de uma DLQ
ficaram presas 40+ minutos sem nenhum worker real pegá-las. Cada Service escala de 0 a N tasks
por profundidade de fila SQS via Application Auto Scaling (target-tracking + step scaling de
cold-start 0→1). **Lambda watchman decommissionada** (2026-07-16, dev e prod) depois de validar
o modelo novo em produção real — nenhuma referência a ela sobra no código nem no pipeline.

**IAM por convenção de nome**: a policy `giom-sqs-fila-videos` (inline em
`giom-worker-task-role`) usa `Resource: arn:aws:sqs:us-east-1:798229612288:giom-videos-*` —
qualquer fila nova que siga o prefixo `giom-videos-*` (qualquer etapa, qualquer canal, dev ou
prod) funciona no primeiro deploy sem precisar editar IAM. Toda infra hoje é provisionada
manualmente via AWS CLI (nada em Terraform/CloudFormation ainda) — backlog de IaC completo
registrado no Board (Feature #91, sob o Épico "Plataforma Giom como produto").
