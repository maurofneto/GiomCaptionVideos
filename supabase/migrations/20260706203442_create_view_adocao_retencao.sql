-- =========================================================
-- View de adoção/retenção semanal do bot — usuários ativos, novos,
-- recorrentes e % de retenção em relação à semana anterior.
-- =========================================================

CREATE OR REPLACE VIEW v_adocao_retencao AS
WITH atividade AS (
    SELECT DISTINCT chat_id, date_trunc('week', criado_em)::date AS semana
    FROM conversa_historico
), primeira AS (
    SELECT chat_id, min(semana) AS primeira_semana
    FROM atividade
    GROUP BY chat_id
), semanas AS (
    SELECT DISTINCT semana FROM atividade
)
SELECT
    semana,
    (SELECT count(*) FROM atividade a WHERE a.semana = s.semana) AS usuarios_ativos,
    (SELECT count(*) FROM atividade a JOIN primeira p ON p.chat_id = a.chat_id
      WHERE a.semana = s.semana AND p.primeira_semana = s.semana) AS usuarios_novos,
    (SELECT count(*) FROM atividade a JOIN primeira p ON p.chat_id = a.chat_id
      WHERE a.semana = s.semana AND p.primeira_semana < s.semana) AS usuarios_recorrentes,
    round(100.0 * (
        SELECT count(*) FROM atividade atual
        WHERE atual.semana = s.semana
          AND atual.chat_id IN (
              SELECT chat_id FROM atividade anterior WHERE anterior.semana = s.semana - interval '7 days'
          )
    ) / NULLIF((
        SELECT count(*) FROM atividade anterior WHERE anterior.semana = s.semana - interval '7 days'
    ), 0), 1) AS retencao_semana_anterior_pct
FROM semanas s
ORDER BY semana;
