# Tracing e custo na pipeline de vídeos (Instituto/Ministério)

Instrumentação de tempo e custo por etapa da pipeline de processamento de vídeos (transcrição,
resumo, categorização, embedding), tanto por vídeo quanto por página (lote de até 50 vídeos
buscado de uma vez na API do YouTube).

## Tracing (Sentry Performance)

`ChannelProcessor` agora abre uma transação Sentry por **página** (`pipeline.pagina`) e uma
etapa (`video`) por vídeo processado dentro dela — mesmo padrão já usado no bot
(`ChatBotProcessor`). Uma vez que a etapa "video" está aberta, qualquer telemetria registrada
durante `IVideoHandler.HandleAsync` (transcrição, resumo, categorização, embedding) se anexa
automaticamente a ela como measurement — o Handler não precisa conhecer `ITracingService`.

Resultado prático: abrindo a transação `pipeline.pagina` no Sentry Performance, cada "video"
child span mostra a duração total daquele vídeo, e dentro dele (via measurements) o tempo de
cada etapa individual.

## Novas measurements disponíveis

| Measurement | Etapa | Custo? |
|---|---|---|
| `measurements.transcricao.tempo_ms` | Obter legenda (YouTube) | Nunca — providers atuais são gratuitos |
| `measurements.resumo.tempo_ms` / `.custo_usd` | Resumo via Claude | Sim (já existia) |
| `measurements.categorizacao.tempo_ms` / `.custo_usd` | Categorização via Claude | Sim (já existia) |
| `measurements.embedding.tempo_ms` / `.custo_usd` | Indexação de chunks | Só com `Embedding:Provider=Direct` (chama a OpenAI aqui mesmo) |

Com `Embedding:Provider=SupabaseEdgeFunction`, `embedding.custo_usd` fica sempre null — a
chamada à OpenAI acontece dentro da Edge Function, invisível pro lado .NET. Instituto usa
`Direct` hoje, então tem custo real de embedding reportado.

## Consultas por vídeo (Supabase — `interacao_log`)

Cada linha já tem `entidade_id = video.Id`, então dá pra somar custo/tempo por vídeo direto:

```sql
select entidade_id as video_id,
  sum(custo_estimado_usd) filter (where tipo = 'resumo') as custo_resumo,
  sum(custo_estimado_usd) filter (where tipo = 'categorizacao') as custo_categorizacao,
  sum(custo_estimado_usd) filter (where tipo = 'embedding') as custo_embedding,
  sum(custo_estimado_usd) as custo_total_pipeline,
  sum(tempo_ms) as tempo_total_ms
from interacao_log
where tipo in ('transcricao', 'resumo', 'categorizacao', 'embedding')
group by entidade_id
order by custo_total_pipeline desc nulls last;
```

## Por que "por página" não está no Supabase

`interacao_log` grava por vídeo (`entidade_id`), não por página — não existe um "id de página"
no schema hoje (a página é só um lote de busca da API do YouTube, não uma entidade
persistida). Agregação por página vive no Sentry: a transação `pipeline.pagina` já soma/mostra
todos os vídeos processados naquele lote. Se precisar de "por página" consultável via SQL no
futuro, o jeito seria persistir um identificador de página em `interacao_log` — não implementado
agora por ser um dado que a pipeline já expõe naturalmente via tracing.
