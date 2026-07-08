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

## Fila de ingestão (`IFilaPublisher<T>` / `IFilaConsumer<T>`)

Abstração genérica no Domain, implementada por `Infrastructure.Sqs` (AWS SQS — cria a fila
sozinha se não existir, via `SqsFilaCanal.ResolverQueueUrlAsync`, idempotente) e
`Infrastructure.RabbitMq` (CloudAMQP/LavinMQ, ambos falam AMQP 0-9-1).

Fluxo: `ChannelProcessor` descobre vídeo novo → `FilaVideoHandler` (decorator de
`IVideoHandler`) publica `VideoParaProcessar` na fila em vez de processar direto →
`VideoQueueConsumer` drena a fila na mesma execução (por enquanto — ver observação de
topologia abaixo) → chama `IVideoProcessador` (role-interface que faz o processamento real:
legenda → resumo → categorização → embedding).

**Por que duas interfaces (`IVideoHandler` / `IVideoProcessador`) e não uma?** Segregação de
papel, mesmo padrão de `ITelemetriaSink`/`ITelemetriaService`: `ChannelProcessor` só enxerga
`IVideoHandler` (publica ou processa, não importa qual) — `FilaVideoHandler` decora e delega
pro `IVideoProcessador` real (`InstitutoVideoHandler`/`MinisterioVideoHandler`). Isso permite
trocar "processa direto" por "publica na fila" **sem tocar em `ChannelProcessor`**.

**Topologia atual**: publish e consume acontecem na mesma execução/processo (o worker
descobre, publica tudo, depois drena tudo). Isso já valida a fila real de ponta a ponta sem
mudar o modelo de deploy (`run-task` sob demanda). Um worker separado por etapa (drenando a
fila continuamente, em processo próprio) é possível depois plugando na mesma interface, sem
mudar `Application`.

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
