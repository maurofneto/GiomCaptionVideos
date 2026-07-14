-- =========================================================
-- View de feedback do bot (👍/👎) por dia — % útil.
-- =========================================================

CREATE OR REPLACE VIEW v_feedback_bot AS
SELECT
    date_trunc('day', criado_em) AS dia,
    count(*) AS total_respostas,
    count(feedback) AS total_avaliadas,
    count(*) FILTER (WHERE feedback = 1) AS util,
    count(*) FILTER (WHERE feedback = -1) AS nao_util,
    round(100.0 * count(*) FILTER (WHERE feedback = 1) / NULLIF(count(feedback), 0), 1) AS pct_util
FROM conversa_historico
WHERE resposta IS NOT NULL
GROUP BY date_trunc('day', criado_em)
ORDER BY date_trunc('day', criado_em) DESC;
