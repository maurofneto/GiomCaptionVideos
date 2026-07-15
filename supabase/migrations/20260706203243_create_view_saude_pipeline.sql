-- =========================================================
-- View de saúde da pipeline do Instituto Giom — % de vídeos relevantes
-- que completaram resumo+categorização+embedding, e atraso médio (horas)
-- entre upload e indexação.
-- =========================================================

CREATE OR REPLACE VIEW v_saude_pipeline AS
WITH relevantes AS (
    SELECT id, uploaded_at
    FROM video
    WHERE channel_id = 'UCyyXFx2xHdzfavx6H6Q8zJw'
), resumo_d AS (
    SELECT DISTINCT video_id FROM resumo
), categoria_d AS (
    SELECT DISTINCT video_id FROM categoria_teologica
), chunk_d AS (
    SELECT video_id, max(indexado_em) AS indexado_em
    FROM video_chunk
    GROUP BY video_id
), completos AS (
    SELECT rel.id, rel.uploaded_at, ch.indexado_em
    FROM relevantes rel
    JOIN resumo_d r ON r.video_id = rel.id
    JOIN categoria_d c ON c.video_id = rel.id
    JOIN chunk_d ch ON ch.video_id = rel.id
)
SELECT
    (SELECT count(*) FROM relevantes) AS total_videos,
    (SELECT count(*) FROM completos) AS videos_completos,
    round(100.0 * (SELECT count(*) FROM completos) / NULLIF((SELECT count(*) FROM relevantes), 0), 1) AS pct_completo,
    round(extract(epoch FROM avg(indexado_em - uploaded_at)) / 3600.0, 1) AS atraso_medio_horas
FROM completos;
