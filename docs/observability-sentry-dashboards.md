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

## Como criar os widgets (manual, feito na UI do Sentry)

Não tenho acesso à API de management do Sentry (só ao SDK de ingestão via DSN) — os
dashboards precisam ser montados manualmente na UI do Sentry:

1. Sentry → **Dashboards** → **Create Dashboard**.
2. Adicionar um widget do tipo **Line Chart** ou **Table**, dataset **Transactions**.
3. Query de exemplo — custo médio de resumo por dia: `avg(measurements.resumo.custo_usd)`,
   agrupado por `transaction` ou por dia.
4. Repetir para os outros pares tipo/measurement da tabela acima (tempo por etapa, custo por
   etapa, tokens por etapa).
5. Tags (`{tipo}.provedor`, `{tipo}.modelo`) servem de filtro/`group by` nos mesmos widgets —
   ex.: comparar custo por modelo, se algum dia dois modelos rodarem em paralelo.

## Por que não breadcrumb

Breadcrumb continua sendo gravado (útil pra contexto quando um erro real é capturado — o
relatório vem com o histórico de operações que aconteceram antes), mas não aparece em
Discover/Dashboards. Measurement/tag numa transação é o mecanismo do Sentry que realmente é
consultável entre muitas transações ao longo do tempo.
