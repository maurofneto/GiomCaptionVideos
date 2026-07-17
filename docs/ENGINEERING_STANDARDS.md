# Padrões de Engenharia

## Nomenclatura

- **Domínio em português** (classes, métodos, mensagens de log, comentários): `Video`,
  `ChannelConfig`, `SessaoObservabilidade`, `IFilaPublisher`, `PublicarAsync`. Termos técnicos
  genéricos (nomes de tipos de framework, siglas de protocolo) ficam em inglês naturalmente.
- **Um projeto `Infrastructure.<Nome>` por dependência externa** — nunca agrupar duas
  dependências não relacionadas no mesmo projeto de infra.
- **Interfaces por papel, não por implementação**: quando responsabilidades distintas
  colidiriam numa única interface, segregue (Interface Segregation) — padrão usado em
  `ITelemetriaSink`/`ITelemetriaService` e, na pipeline de vídeos, em
  `IEtapaTranscricaoHandler`/`IEtapaResumoHandler`/`IEtapaCategorizacaoHandler`/
  `IEtapaEmbeddingHandler` (cada canal implementa só a etapa que usa — Ministério não
  implementa Categorização/Embedding, sem métodos vazios "porque a interface obriga").
- **Erro de provedor externo vira exceção de Domain na borda do adapter, não uma exceção
  específica de SDK vazando pra cima**: cada `Infrastructure.<Provedor>` mapeia o formato de
  erro (HTTP status, tipo de exceção do SDK) do SEU provedor pra um tipo em
  `Domain/Exceptions/` (ex: `RateLimitExcedidoException`), permitindo que quem consome (a
  fila, `SqsFilaConsumer`) decida o que fazer sem conhecer YouTube/Claude/OpenAI.

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
  `tests/Giom.VideoManager.SmokeTests` (vídeos fake, sem depender do YouTube real).

## Secrets

- **Nunca** commitar valor real de secret. `appsettings.json` só tem placeholder em branco
  com comentário apontando o path SSM real (`/giom/{dev,prod}/.../Chave`).
- Exceção deliberada: `tests/Giom.VideoManager.SmokeTests` é dev-only e pode ter
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

## Migrations (Supabase)

- **Regra definitiva (ver Task #108 e incidente real de 2026-07-17):** o histórico remoto que
  `supabase db push` consulta (tabela `supabase_migrations.schema_migrations`) casa migrations
  com arquivos locais **só pelo timestamp no nome do arquivo** — nunca pelo conteúdo. Se você
  aplicar uma mudança de schema via MCP do Supabase (`apply_migration`) — o caminho mais comum
  neste projeto pra iterar rápido em dev — o MCP gera o próprio timestamp (hora exata da
  chamada). Ao commitar o arquivo `.sql` correspondente no `supabase/migrations/`, o nome do
  arquivo **tem que usar exatamente esse mesmo timestamp**, não um escolhido à mão. Pra
  descobrir o timestamp real que ficou gravado: `list_migrations` (MCP) ou
  `supabase migration list` (CLI) logo depois de aplicar.
  - **Sintoma de quando isso já deu errado**: `supabase db push` (rodado no pipeline, stage
    `Migrations`) falha com "Remote migration versions not found in local migrations
    directory" — nesse caso, compare `list_migrations` (remoto) com `ls supabase/migrations/`
    (local); a correção geralmente é só renomear (`git mv`) o arquivo local pro timestamp que o
    remoto já tem — nunca usar `supabase migration repair` sem entender que ele só edita a
    tabela de histórico, não desfaz nem refaz o DDL que já rodou (migrations não-idempotentes,
    ex.: `DROP CONSTRAINT` sem `IF EXISTS`, falhariam se reaplicadas).
  - Prefira, quando a mudança não precisar de iteração ao vivo contra o banco: escrever o
    arquivo de migration primeiro (com o timestamp escolhido você mesmo), commitar, e deixar
    `supabase db push` (local ou no pipeline) aplicar — isso elimina a categoria inteira de
    descompasso, porque o arquivo passa a ser a única fonte da verdade desde o início.

## Observabilidade — o que instrumentar

- Toda etapa nova da pipeline (nova etapa de processamento, novo tipo de chamada LLM) ganha
  span (`ITracingService`) **e** métrica de tempo/custo (`ITelemetriaService`) — traces sozinhos
  não bastam pra dashboard agregado.
- Toda sessão de observabilidade (`SessaoObservabilidade`) carrega `CorrelationId` — é o que
  correlaciona Sentry, Helicone, logs de console e CloudWatch pro mesmo evento.
