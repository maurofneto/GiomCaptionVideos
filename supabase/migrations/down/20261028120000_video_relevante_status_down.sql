-- DOWN de 20261028120000: remove as colunas da Fase B. Reverter o código antes (a versão nova grava essas colunas).
DROP INDEX IF EXISTS idx_video_channel_relevante;
DROP TRIGGER IF EXISTS trg_video_updated_at ON video;
DROP FUNCTION IF EXISTS video_set_updated_at();
ALTER TABLE video DROP COLUMN IF EXISTS updated_at;
ALTER TABLE video DROP CONSTRAINT IF EXISTS video_status_check;
ALTER TABLE video DROP COLUMN IF EXISTS status;
ALTER TABLE video DROP COLUMN IF EXISTS relevante;
