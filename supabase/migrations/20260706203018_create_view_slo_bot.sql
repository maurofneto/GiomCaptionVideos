-- =========================================================
-- View de SLOs numéricos do bot (latência p95, taxa de busca vazia,
-- taxa de incerteza, % útil) na janela dos últimos 7 dias.
-- =========================================================

CREATE OR REPLACE VIEW v_slo_bot AS
WITH janela AS (
    SELECT now() - interval '7 days' AS desde
), latencia AS (
    SELECT percentile_cont(0.95) WITHIN GROUP (ORDER BY interacao_log.tempo_ms::double precision) AS p95_ms
    FROM interacao_log, janela
    WHERE interacao_log.tipo = 'bot_interacao'
      AND interacao_log.sucesso = true
      AND interacao_log.criado_em >= janela.desde
), busca AS (
    SELECT count(*) AS total,
           count(*) FILTER (WHERE (interacao_log.detalhes ->> 'chunks')::int = 0) AS vazias
    FROM interacao_log, janela
    WHERE interacao_log.tipo = 'busca_rag'
      AND interacao_log.criado_em >= janela.desde
), incerteza AS (
    SELECT count(*) AS total,
           count(*) FILTER (WHERE (interacao_log.detalhes ->> 'incerteza')::boolean IS TRUE) AS incertas
    FROM interacao_log, janela
    WHERE interacao_log.tipo = 'bot_resposta'
      AND interacao_log.sucesso = true
      AND interacao_log.criado_em >= janela.desde
), util AS (
    SELECT COALESCE(sum(v_feedback_bot.total_avaliadas), 0) AS total_avaliadas,
           COALESCE(sum(v_feedback_bot.util), 0) AS util
    FROM v_feedback_bot, janela
    WHERE v_feedback_bot.dia >= date_trunc('day', janela.desde)
)
SELECT
    round(latencia.p95_ms) AS latencia_p95_ms,
    15000 AS latencia_p95_alvo_ms,
    round(100.0 * busca.vazias / NULLIF(busca.total, 0), 1) AS taxa_busca_vazia_pct,
    15.0 AS taxa_busca_vazia_alvo_pct,
    round(100.0 * incerteza.incertas / NULLIF(incerteza.total, 0), 1) AS taxa_incerteza_pct,
    10.0 AS taxa_incerteza_alvo_pct,
    round(100.0 * util.util / NULLIF(util.total_avaliadas, 0), 1) AS pct_util,
    80.0 AS pct_util_alvo
FROM latencia, busca, incerteza, util;
