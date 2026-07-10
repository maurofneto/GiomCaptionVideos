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
delegate da etapa, relança erro pro retry). Dependências reais entre etapas (não é corrente
linear): Resumo depende de Transcrição; Categorização depende de Resumo; **Embedding depende
só de Transcrição** (indexa a transcrição completa, não o resumo) — por isso Transcrição faz
**fan-out** pra Resumo e Embedding em paralelo.

```
video.transcricao ─┬─> video.resumo ──> video.categorizacao
                    └─> video.embedding
```

**DI — Keyed Services (.NET 9)**: cada etapa é uma chave (`"transcricao"`, `"resumo"`...) que
identifica publisher/consumer/canal daquele estágio, sem precisar de uma classe por canal.
`Composition.AddFilaVideoEtapa(chave, config, secaoRaiz)` decide Sqs/RabbitMq lendo
`{secaoRaiz}:Provider`; `Application.AddEtapaConsumer<THandler>(chave, nomeSpan, processar)`
registra o `EtapaQueueConsumer` pronto, resolvendo `THandler` só na hora de processar
(`Program.cs` de cada worker cai pra 2 linhas por etapa: `AddFilaVideoEtapa` +
`AddEtapaConsumer`).

**`InstitutoPipelineHandler` / `MinisterioPipelineHandler`** — único handler ativo por canal.
Cada um é dono da própria topologia de descoberta (`IVideoHandler.IsRelevant`/`HandleAsync`)
**e** implementa as interfaces de etapa que faz sentido pro seu `ProcessingOptions`
(`IEtapaTranscricaoHandler`/`IEtapaResumoHandler`/`IEtapaCategorizacaoHandler`/
`IEtapaEmbeddingHandler` — Ministério só as duas primeiras, ISP). Não existe mais um
`FilaVideoHandler` genérico impondo a mesma descoberta pra todo canal — cada orquestrador
decide sozinho o que publicar em seguida.

**Histórico/rollback**: `InstitutoVideoHandler`/`MinisterioVideoHandler` (processamento
monolítico antigo, legenda→resumo→categorização→embedding numa chamada só) e
`IVideoProcessador` continuam no repositório, intocados, mas **não registrados em nenhum
DI** — só documentação viva de como era antes da Feature #33, caso um rollback precise deles.
`FilaVideoHandler` e `VideoQueueConsumer` (a versão anterior, especial pra transcrição) foram
removidos de vez, substituídos pelo mecanismo genérico acima.

**Topologia de execução**: publish e consume dos 4 consumers acontecem na mesma
execução/processo, **concorrentes** (`Task.WhenAll`, não sequencial) — Resumo/Embedding
começam assim que o primeiro vídeo é transcrito, sem esperar o lote inteiro de Transcrição
terminar. Ainda é `run-task` sob demanda, sem worker de longa duração por etapa.

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

Conta `798229612288`, região `us-east-1`, cluster ECS `giom-cluster`. `Instituto.Worker` e
`Ministerio.Worker` rodam como ECS Task sob demanda (`aws ecs run-task`), sem agendamento
automático configurado até o momento — cada run precisa ser disparada manualmente ou por
alguma automação futura (ver Board, Feature "Escala e cobertura de vídeos" / US #20).
`AgenteInstituto.Worker` roda como ECS Service de longa duração (um por ambiente).

**IAM por convenção de nome**: a policy `giom-sqs-fila-videos` (inline em
`giom-worker-task-role`) usa `Resource: arn:aws:sqs:us-east-1:798229612288:giom-videos-*` —
qualquer fila nova que siga o prefixo `giom-videos-*` (qualquer etapa, qualquer canal, dev ou
prod) funciona no primeiro deploy sem precisar editar IAM. Toda infra hoje é provisionada
manualmente via AWS CLI (nada em Terraform/CloudFormation ainda) — backlog de IaC completo
registrado no Board (Feature #91, sob o Épico "Plataforma Giom como produto").
