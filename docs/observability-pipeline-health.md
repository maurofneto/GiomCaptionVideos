# Saúde do pipeline de vídeos (Instituto Giom)

Indicadores de quão em dia está o conteúdo que alimenta o RAG do bot, sem precisar repetir a
auditoria manual feita anteriormente (ver item #27 do Board, fechado como pontual).
Consultável a qualquer momento via `select * from v_saude_pipeline;` (Supabase, prod e dev).

| Coluna | Significado |
|---|---|
| `total_videos` | Total de vídeos do canal do Instituto (único canal relevante — o Ministério não alimenta o RAG) |
| `videos_completos` | Quantos têm resumo + categoria teológica + chunks indexados, os 3 completos |
| `pct_completo` | `videos_completos / total_videos` — % do catálogo pesquisável pelo bot hoje |
| `atraso_medio_horas` | Tempo médio entre `video.uploaded_at` e o último chunk indexado (`video_chunk.indexado_em`) |

## Leitura do valor atual (prod, 2026-07-06)

`pct_completo = 86.7%` (130 de 150 vídeos) — 20 vídeos do catálogo ainda não têm chunk/resumo/categoria.

`atraso_medio_horas ≈ 12820h` (~534 dias) — **não interpretar como "tempo normal de indexação de
um vídeo novo"**: o valor está inflado pelo backfill inicial do catálogo (vídeos antigos
publicados há anos e só processados quando o pipeline entrou em operação). É útil para
acompanhar a tendência ao longo do tempo, mas hoje mistura backfill histórico com o fluxo
normal de vídeos novos — dá pra revisar depois calculando a média só sobre vídeos publicados
nos últimos N dias, quando o catálogo estiver mais perto de 100% completo.

## View

Definida na migration `create_view_saude_pipeline` (aplicada em prod `rhjsjfbegtusucunkgav` e
dev `fajrstyqgogmqzceujjt`). Canal do Instituto (`UCyyXFx2xHdzfavx6H6Q8zJw`) está fixo na view
— mesmo valor hardcoded já usado em `Rag:CanalId` no worker do bot.
