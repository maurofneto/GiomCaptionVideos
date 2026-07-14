-- =========================================================
-- Permite resposta NULL em conversa_historico — necessário pra registrar
-- a pergunta antes da resposta do bot existir (ex.: erro/timeout no meio do
-- processamento), sem violar NOT NULL.
-- =========================================================

ALTER TABLE conversa_historico ALTER COLUMN resposta DROP NOT NULL;
