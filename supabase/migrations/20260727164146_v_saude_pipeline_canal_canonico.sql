-- Corrige v_saude_pipeline_todos_canais: agrupar por (channel_id, channel_title) fragmentava o
-- canal Instituto em 2 linhas, porque video.channel_title é inconsistente (nome do canal do
-- YouTube mudou/foi gravado diferente em parte das linhas). channel_id é o identificador
-- estável; o nome canônico do canal vem de canal_ingestao.nome (US #105), não de video.
--
-- Também é aqui que o filtro de data do atraso médio entra em vigor pra valer: vídeos vão até
-- 2022 no histórico (quase 4 anos de backlog importado) — média de atraso sem filtro incluiria
-- o processamento em lote do backlog inicial, não representativo da operação do dia a dia.
-- Filtro (uploaded_at >= 2026-07-01) fica só na coluna atraso_medio_horas (FILTER), não no
-- total_videos/pct_completo, que continuam refletindo a saúde histórica completa do canal.

DROP VIEW IF EXISTS v_saude_pipeline;

CREATE VIEW v_saude_pipeline AS
WITH relevantes AS (
    SELECT id, channel_id, uploaded_at
    FROM video
), resumo_d AS (
    SELECT DISTINCT video_id FROM resumo
), categoria_d AS (
    SELECT DISTINCT video_id FROM categoria_teologica
), chunk_d AS (
    SELECT video_id, max(indexado_em) AS indexado_em
    FROM video_chunk
    GROUP BY video_id
), completos AS (
    SELECT rel.id, rel.channel_id, rel.uploaded_at, ch.indexado_em
    FROM relevantes rel
    JOIN resumo_d r ON r.video_id = rel.id
    JOIN categoria_d c ON c.video_id = rel.id
    JOIN chunk_d ch ON ch.video_id = rel.id
)
SELECT
    rel.channel_id,
    COALESCE(ci.nome, rel.channel_id) AS canal,
    count(*) AS total_videos,
    count(comp.id) AS videos_completos,
    round(100.0 * count(comp.id) / NULLIF(count(*), 0), 1) AS pct_completo,
    round(extract(epoch FROM avg(comp.indexado_em - comp.uploaded_at) FILTER (WHERE comp.uploaded_at >= DATE '2026-07-01')) / 3600.0, 1) AS atraso_medio_horas
FROM relevantes rel
LEFT JOIN completos comp ON comp.id = rel.id
LEFT JOIN canal_ingestao ci ON ci.channel_id = rel.channel_id
GROUP BY rel.channel_id, ci.nome
ORDER BY canal;
