-- =========================================================
-- Baseline (US #149): chat_sessao, conversa_historico e interacao_log foram
-- criadas fora de migration no projeto de produção. Sem isto, o replay do
-- histórico numa branch nova do Supabase falha aqui. IF NOT EXISTS mantém a
-- migration inofensiva onde as tabelas já existem (produção).
-- =========================================================
CREATE TABLE IF NOT EXISTS chat_sessao (
  chat_id text PRIMARY KEY,
  geracao integer NOT NULL DEFAULT 1,
  atualizado_em timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE chat_sessao ENABLE ROW LEVEL SECURITY;

CREATE TABLE IF NOT EXISTS conversa_historico (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  chat_id text NOT NULL,
  pergunta text NOT NULL,
  resposta text NOT NULL,
  criado_em timestamptz NOT NULL DEFAULT now(),
  geracao integer NOT NULL DEFAULT 1,
  feedback smallint CHECK (feedback = ANY (ARRAY[1, -1])),
  reasoning_content text
);
CREATE INDEX IF NOT EXISTS conversa_historico_chat_id_idx ON conversa_historico (chat_id, criado_em DESC);
CREATE INDEX IF NOT EXISTS idx_conversa_historico_chat_geracao ON conversa_historico (chat_id, geracao, criado_em);
ALTER TABLE conversa_historico ENABLE ROW LEVEL SECURITY;

CREATE TABLE IF NOT EXISTS interacao_log (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tipo text NOT NULL,
  entidade_id text NOT NULL,
  provedor text,
  modelo text,
  tempo_ms integer NOT NULL,
  tokens_entrada integer,
  tokens_saida integer,
  custo_estimado_usd numeric(10,6),
  sucesso boolean NOT NULL DEFAULT true,
  detalhes jsonb,
  criado_em timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_interacao_log_tipo_criado ON interacao_log (tipo, criado_em DESC);
CREATE INDEX IF NOT EXISTS idx_interacao_log_entidade ON interacao_log (entidade_id);
ALTER TABLE interacao_log ENABLE ROW LEVEL SECURITY;

-- =========================================================
-- Permite resposta NULL em conversa_historico — necessário pra registrar
-- a pergunta antes da resposta do bot existir (ex.: erro/timeout no meio do
-- processamento), sem violar NOT NULL.
-- =========================================================

ALTER TABLE conversa_historico ALTER COLUMN resposta DROP NOT NULL;
