# Padrões de Engenharia

## Nomenclatura

- **Domínio em português** (classes, métodos, mensagens de log, comentários): `Video`,
  `ChannelConfig`, `SessaoObservabilidade`, `IFilaPublisher`, `PublicarAsync`. Termos técnicos
  genéricos (nomes de tipos de framework, siglas de protocolo) ficam em inglês naturalmente.
- **Um projeto `Infrastructure.<Nome>` por dependência externa** — nunca agrupar duas
  dependências não relacionadas no mesmo projeto de infra.
- **Interfaces por papel, não por implementação**: `IVideoHandler` (papel genérico) vs.
  `IVideoProcessador` (papel específico de "processamento real") — quando duas
  responsabilidades distintas colidiriam numa única interface, segregue (Interface
  Segregation), seguindo o padrão já usado em `ITelemetriaSink`/`ITelemetriaService`.

## Código

- **Não adicionar abstração além do que a tarefa pede.** Três linhas parecidas são melhores
  que uma abstração prematura. Sem feature flags ou fallback pra cenário que não existe.
- **Não modificar uma classe orquestradora estável pra plugar uma feature nova** — prefira um
  decorator sobre a interface que ela já injeta (ver `docs/ARCHITECTURE.md`, exemplo real:
  `FilaVideoHandler` decorando `IVideoHandler` em vez de `ChannelProcessor` aprender sobre
  filas).
- **DI sem `new` solto** — toda dependência é resolvida via `IServiceCollection`, registrada
  no `ServiceCollectionExtensions` do projeto que a implementa.
- **Comentários só quando o "porquê" não é óbvio** — não descrever o que o código já deixa
  claro pelo nome. Comentário útil: por que uma decisão não-óbvia foi tomada, uma
  invariante escondida, um workaround de bug específico de biblioteca.

## Testes

- `Giom.VideoManager.Testes.Comuns` centraliza fakes/test doubles reutilizáveis (`FakeFila*`,
  `FakeTracingService`, `FakeTelemetriaService` etc.) — antes de criar um mock novo, checar se
  já existe um fake lá.
- Todo comportamento novo ganha teste; toda mudança precisa rodar `dotnet test` limpo antes de
  PR (checklist do template de PR cobra isso explicitamente).
- SQS/serviços sem SDK de fake oficial simples: cobrir com testes unitários pontuais
  (serialização, mapeamento de opções) + validação manual end-to-end via
  `tools/Giom.VideoManager.ObservabilidadeTeste` (vídeos fake, sem depender do YouTube real).

## Secrets

- **Nunca** commitar valor real de secret. `appsettings.json` só tem placeholder em branco
  com comentário apontando o path SSM real (`/giom/{dev,prod}/.../Chave`).
- Exceção deliberada: `tools/Giom.VideoManager.ObservabilidadeTeste` é dev-only e pode ter
  valores reais de dev commitados (queue name, URLs públicas) — nunca segredo de prod.
- Ao lidar com um token/segredo colado em chat ou API de terceiro (ex: token de API do
  Sentry): nunca ecoar o valor em nenhuma resposta; guardar só em arquivo de scratchpad fora
  do repo; usar via `curl`/substituição de shell, nunca digitado literalmente num comando.
- Cache local de OAuth (`Google.Apis.Auth.*/`) fica coberto pelo `.gitignore` — confirmar
  antes de assumir que está seguro se um padrão novo de cache aparecer.

## AWS CLI (Windows/Git Bash)

- Qualquer argumento do AWS CLI começando com `/` (ex: `--name "/giom/dev/..."`) é reescrito
  silenciosamente pelo MSYS do Git Bash como caminho do Windows, quebrando a chamada. Prefixar
  com `MSYS_NO_PATHCONV=1`.
- Preferir `--query` do AWS CLI pra filtrar no servidor em vez de trazer o JSON inteiro e
  filtrar depois (ver `CLAUDE.md`, seção de economia de tokens).

## Observabilidade — o que instrumentar

- Toda etapa nova da pipeline (nova etapa de processamento, novo tipo de chamada LLM) ganha
  span (`ITracingService`) **e** métrica de tempo/custo (`ITelemetriaService`) — traces sozinhos
  não bastam pra dashboard agregado.
- Toda sessão de observabilidade (`SessaoObservabilidade`) carrega `CorrelationId` — é o que
  correlaciona Sentry, Helicone, logs de console e CloudWatch pro mesmo evento.
