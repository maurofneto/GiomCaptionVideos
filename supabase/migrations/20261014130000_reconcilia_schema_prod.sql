-- =========================================================
-- Reconcilia o schema criado só por migrations com o de PRODUÇÃO (US #149).
--
-- Estes objetos existem em produção mas nunca tiveram migration (aplicados à mão antes do
-- Supabase Branching). Uma branch nova do Supabase é montada só com as migrations, então ficava
-- sem eles: a busca híbrida falhava (video_chunk.conteudo_tsv inexistente) e a categorização
-- multi-área quebrava (UNIQUE(video_id) em categoria_teologica; coluna peso inexistente).
--
-- 100% idempotente: em produção é no-op. Não toca em dado.
-- =========================================================

-- 1. categoria_teologica: um vídeo pode ter várias áreas, cada uma com peso (0.01 a 1.00).
ALTER TABLE categoria_teologica DROP CONSTRAINT IF EXISTS categoria_teologica_video_id_key;

ALTER TABLE categoria_teologica
    ADD COLUMN IF NOT EXISTS peso numeric(3,2) NOT NULL DEFAULT 1.00;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'categoria_teologica_peso_check'
                   AND conrelid = 'public.categoria_teologica'::regclass) THEN
        ALTER TABLE categoria_teologica
            ADD CONSTRAINT categoria_teologica_peso_check CHECK (peso >= 0.01 AND peso <= 1.00);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_categoria_video_peso ON categoria_teologica (video_id, peso DESC);

-- 2. video_chunk: coluna gerada de full-text (usada pela busca híbrida) + índice GIN.
ALTER TABLE video_chunk
    ADD COLUMN IF NOT EXISTS conteudo_tsv tsvector
    GENERATED ALWAYS AS (to_tsvector('portuguese', conteudo)) STORED;

CREATE INDEX IF NOT EXISTS video_chunk_conteudo_tsv_idx ON video_chunk USING gin (conteudo_tsv);
