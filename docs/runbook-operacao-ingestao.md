# Runbook — operação da ingestão, recuperação de vídeos e backlog

US #152. Como a ingestão decide o que processar, o que deu errado em 10/2026, e o passo a passo das operações manuais
usadas na recuperação. Premissa do projeto: comportamento que muda sem deploy vive no banco (`docs/CONFIG_FIRST.md`).

## Como a ingestão decide
- Roda todo dia às **05:00 BRT** (EventBridge `giom-ingestao-daily`, task ECS `giom-ingestao-task`), canal a canal, a partir de
  `canal_ingestao` (regra de relevância, `privacy_status_filtro`, `max_videos_per_run`).
- Percorre a playlist de uploads **inteira**, do mais novo ao mais antigo. Para cada vídeo relevante, `JaProcessadoAsync`
  consulta `video.processed_at`: se nulo, enfileira em `giom-videos-transcricao-prod`.
- `processed_at` só é marcado pelo orquestrador quando **todos** os ramos terminais da topologia (`etapa_topologia`)
  concluem. Vídeo que falha no meio continua com `processed_at` nulo e é reenfileirado na execução seguinte.
- `processamento_progresso.last_processed_date` é só informação: **não filtra mais nada** (ver incidente).
- Ministério: título começa com `GIOM`, só vídeo `public`, `max_videos_per_run = 32`. Instituto: lives `unlisted`, teto 25.

## Incidente de 10/2026 — vídeos perdidos (corrigido no PR 115)
- **Sintoma:** o Ministério não tinha nenhum culto indexado desde 10/09, e havia queda mensal desde julho.
- **Causa:** a ingestão avançava `last_processed_date` quando o vídeo era *enfileirado* e pulava tudo com data igual ou menor.
  Vídeo que falhava depois (sem cookies do yt-dlp, DLQ) ficava abaixo da data e nunca mais era reavaliado. Além disso a DLQ
  retém mensagens por no máximo 14 dias.
- **Impacto:** 22 cultos do Ministério e 3 aulas do Instituto fora do banco.
- **Recuperação:** redrive da DLQ (9 vídeos), recuo manual da data (13 do Ministério) e uma ingestão manual depois do PR 115
  (14 do Ministério + 3 do Instituto). 4 vídeos não listados do Ministério foram removidos do índice.
- **Lição:** o critério de "já processado" tem de ser o resultado final do pipeline (`processed_at`), nunca o ato de enfileirar.

## Operações

### Redrive da DLQ de transcrição
```
aws sqs start-message-move-task --source-arn arn:aws:sqs:us-east-1:798229612288:giom-videos-transcricao-prod-dlq
```
Antes, olhe o conteúdo (`receive-message --visibility-timeout 1`, sem apagar). A mesma mensagem pode aparecer mais de uma vez
na DLQ; o pipeline deduplica pelo `processed_at`.

### Disparar a ingestão à mão (execução única)
`aws ecs run-task --cluster giom-cluster --task-definition giom-ingestao-task --launch-type FARGATE` com a rede da schedule
(`aws scheduler get-schedule --group-name giom-schedules --name giom-ingestao-daily`, campo `EcsParameters.NetworkConfiguration`).
Logs: `/ecs/giom-ingestao`, stream `ecs/giom-ingestao/<taskId>`; procure `processando vídeo` e `playlist percorrida até o fim`.
Depois acompanhe as filas (`transcricao`, `resumo`, `categorizacao`, `embedding`, `etapa-concluida`) até zerarem e a DLQ ficar em 0.

### Auditar a cobertura contra o YouTube (sem chave de API)
`yt-dlp --flat-playlist -J "https://www.youtube.com/channel/<ChannelId>/streams"` e `.../videos` listam os vídeos **públicos**
(a playlist `UU...` pública trunca em 100). Compare os ids com `video` do canal. Em 19/10/2026: 606 públicos no Ministério,
todos no banco depois da ingestão. Vídeos não listados só aparecem com a API autenticada do dono.

### Remover um vídeo do índice
Apague nesta ordem, por `video_id`: `video_chunk`, `categoria_teologica`, `video_etapa_terminal`, `video`. Confirme antes que o
vídeo é do canal e da privacidade esperados.

### Conferir embeddings por canal e origem
Conte chunks por vídeo e origem (`OpenAI`, `VoyageAI`, `Cohere`; devem estar as 3). Teste a busca com um vetor já gravado:
`rpc/match_chunks` com `canal_id_filtro` e `origem_filtro` — deve devolver só conteúdo do canal filtrado.

### Backlog do Ministério (cultos antigos sem chunks)
Cultos antigos foram marcados como processados antes de o canal ter transcrição/embedding. Para reprocessar em lotes **sem
deploy**: limpar `video.processed_at` de até N cultos públicos, ≥ 5 min e sem nenhum chunk (mais novos primeiro); a ingestão
das 05:00 BRT os pega (limite = `max_videos_per_run`). Hoje isso é feito por um script **fora do repositório**
(`~/.aws/scripts/reenfileirar-backlog-ministerio.py`, lote 32, com `--dry-run`) chamado por uma tarefa agendada do Claude
(`backlog-ministerio-diario`, ~04:30). Para pausar: desative a tarefa. Equivalente em SQL:
```sql
UPDATE video SET processed_at = NULL WHERE id IN (
  SELECT v.id FROM video v
   WHERE v.channel_id = 'UCyHMyZH5oon8jW7y_yMRstQ' AND v.privacy_status = 'public' AND v.processed_at IS NOT NULL
     AND v.length >= '00:05:00' AND NOT EXISTS (SELECT 1 FROM video_chunk c WHERE c.video_id = v.id)
   ORDER BY v.uploaded_at DESC LIMIT 32);
```
Limites: a cota da API do YouTube (10.000/dia por projeto, compartilhada pelos dois canais) não pesa no Ministério (legenda por
`YouTubeTranscript`/yt-dlp, sem cota); o teto é prudência (anti-bot e custo).

## Estado em 19/10/2026
Ministério com 31 cultos recentes indexados (OpenAI, VoyageAI, Cohere) e 569 em backlog; Instituto com 260 de 269 longos.
O bot atual atende só o Instituto (`Rag:CanalId` no `AgenteInstituto.Worker`); o Ministério precisa de um segundo bot.
