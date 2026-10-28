-- =========================================================
-- Fase B da regra de relevância (#184): a ingestão passa a gravar todos os vídeos das páginas da playlist que
-- percorre, com o veredito da regra de relevância (canal_ingestao.regra_relevancia) avaliado a cada passagem. O
-- processamento continua só nos relevantes.
--
-- As colunas nascem vazias: vídeos antigos só ganham valor quando a ingestão passa pela página deles (a partir
-- da primeira execução depois do deploy). Esta migration NÃO muda policy, views nem funções que leem `video`;
-- isso fica para a etapa seguinte, depois de conferir os dados (#184, PR B2).
-- =========================================================
ALTER TABLE video ADD COLUMN IF NOT EXISTS relevante BOOLEAN;
ALTER TABLE video ADD COLUMN IF NOT EXISTS status    TEXT NOT NULL DEFAULT 'active';

ALTER TABLE video DROP CONSTRAINT IF EXISTS video_status_check;
ALTER TABLE video ADD CONSTRAINT video_status_check CHECK (status IN ('active', 'removed'));

-- updated_at: última alteração da linha. Os vídeos que já existem partem da data do último processamento (ou da
-- criação), não da data desta migration; o gatilho vem depois do preenchimento, para não sobrescrevê-lo.
-- (created_at, que já existe, é o "inserido em".)
ALTER TABLE video ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ;
UPDATE video SET updated_at = COALESCE(processed_at, created_at) WHERE updated_at IS NULL;
ALTER TABLE video ALTER COLUMN updated_at SET DEFAULT NOW();
ALTER TABLE video ALTER COLUMN updated_at SET NOT NULL;

CREATE OR REPLACE FUNCTION video_set_updated_at() RETURNS trigger
LANGUAGE plpgsql
SET search_path = ''
AS $$
BEGIN
    NEW.updated_at := NOW();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_video_updated_at ON video;
CREATE TRIGGER trg_video_updated_at BEFORE UPDATE ON video
    FOR EACH ROW EXECUTE FUNCTION video_set_updated_at();

COMMENT ON COLUMN video.relevante IS 'Veredito da regra de relevância do canal na última vez que a ingestão visitou o vídeo (#184). NULL = ainda não visitado.';
COMMENT ON COLUMN video.status IS 'active ou removed (o YouTube devolveu "Deleted video" para este vídeo). Um vídeo removido mantém seus dados (#184).';
COMMENT ON COLUMN video.updated_at IS 'Última alteração da linha (gatilho).';

CREATE INDEX IF NOT EXISTS idx_video_channel_relevante ON video (channel_id, relevante);
