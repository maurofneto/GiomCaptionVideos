# Dados de telemetria no Sentry (Performance + Dashboards)

Antes: `SentryTelemetriaService` só gravava um breadcrumb — que só aparece atrelado a um
evento de erro específico (não é possível consultar/agregar breadcrumbs entre várias
transações). Agora, toda chamada a `ITelemetriaService.RegistrarAsync` também grava, na span
ativa no momento (`SentrySdk.GetSpan()`), **measurements** e **tags** — que são dados de
Performance, consultáveis em Discover e usáveis em Dashboards.

## Convenção de nomes

Toda measurement/tag é prefixada pelo `Tipo` do registro de telemetria (`busca_rag`,
`bot_resposta`, `resumo`, `categorizacao`, etc.), pra não colidir quando mais de um tipo de
operação contribui pra mesma transação (ex.: um vídeo com transcrição + resumo +
categorização).

| Measurement | Significado |
|---|---|
| `measurements.{tipo}.tempo_ms` | Duração da operação |
| `measurements.{tipo}.tokens_entrada` | Tokens de entrada (quando aplicável) |
| `measurements.{tipo}.tokens_saida` | Tokens de saída (quando aplicável) |
| `measurements.{tipo}.custo_usd` | Custo estimado em USD (quando aplicável) |

| Tag | Significado |
|---|---|
| `{tipo}.sucesso` | `"True"`/`"False"` |
| `{tipo}.provedor` | Ex.: `"Claude"` |
| `{tipo}.modelo` | Ex.: `"claude-sonnet-5"` |

## Dashboard "Giom - Observabilidade" (já criado)

Criado via API do Sentry (`POST /api/0/organizations/{org}/dashboards/`, usando um Auth Token
de organização — diferente do DSN, que só serve pra ingestão) — não precisou ser montado à mão
na UI. Acesse em https://furigo-neto-tecnologia.sentry.io/dashboard/7977167/

Widgets:
- **Latência do bot (p95)** — `p95(transaction.duration)`, `transaction:bot.interacao`
- **Custo do bot por dia (Claude)** — `sum(measurements.bot_resposta.custo_usd)`
- **Tokens do bot por dia** — entrada/saída, `measurements.bot_resposta.tokens_*`
- **Tempo por etapa da pipeline (média)** — transcrição/resumo/categorização/embedding
- **Custo por etapa da pipeline (soma diária)** — resumo/categorização/embedding

Ponto técnico: o dataset "transactions" da API do Sentry está em depreciação — os widgets
usam `widgetType: "spans"` com `is_transaction:true` na condição em vez de
`widgetType: "transaction-like"` (que a API rejeita hoje com esse aviso).

Pra criar mais widgets/dashboards depois (manual, na UI):
1. Sentry → **Dashboards** → **Create Dashboard**.
2. Widget tipo **Line Chart**, dataset **Spans**, com `is_transaction:true` na query.
3. Campo de exemplo — custo médio de resumo por dia: `avg(measurements.resumo.custo_usd)`.
4. Tags (`{tipo}.provedor`, `{tipo}.modelo`) servem de filtro/`group by` nos mesmos widgets.

## Por que não breadcrumb

Breadcrumb continua sendo gravado (útil pra contexto quando um erro real é capturado — o
relatório vem com o histórico de operações que aconteceram antes), mas não aparece em
Discover/Dashboards. Measurement/tag numa transação é o mecanismo do Sentry que realmente é
consultável entre muitas transações ao longo do tempo.
