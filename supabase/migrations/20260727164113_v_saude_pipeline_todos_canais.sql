-- v_saude_pipeline estava hardcoded pro canal Instituto (channel_id fixo no WHERE) — corrige
-- pra mostrar todos os canais, agrupando por canal em vez de filtrar um só.
--
-- OBS: esta versão intermediária agrupava por (channel_id, channel_title) e fragmentou o canal
-- Instituto em 2 linhas (channel_title inconsistente na tabela video: "Dr. Wagner Cordeiro" em
-- boa parte das linhas, "Instituto Giom" em outras) — corrigido na migration seguinte
-- (v_saude_pipeline_canal_canonico), que usa canal_ingestao.nome como fonte canônica do nome.
-- Mantida aqui como arquivo próprio pra bater com o histórico real do Supabase (duas chamadas
-- de apply_migration em sequência, mesmo padrão de reconciliação do Bug #132).

DROP VIEW IF EXISTS v_saude_pipeline;

CREATE VIEW v_saude_pipeline AS
WITH relevantes AS (
    SELECT id, channel_id, channel_title, uploaded_at
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
    rel.channel_title,
    count(*) AS total_videos,
    count(comp.id) AS videos_completos,
    round(100.0 * count(comp.id) / NULLIF(count(*), 0), 1) AS pct_completo,
    round(extract(epoch FROM avg(comp.indexado_em - comp.uploaded_at) FILTER (WHERE comp.uploaded_at >= DATE '2026-07-01')) / 3600.0, 1) AS atraso_medio_horas
FROM relevantes rel
LEFT JOIN completos comp ON comp.id = rel.id
GROUP BY rel.channel_id, rel.channel_title
ORDER BY rel.channel_title;
