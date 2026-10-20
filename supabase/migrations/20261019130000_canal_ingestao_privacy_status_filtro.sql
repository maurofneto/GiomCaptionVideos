-- =========================================================
-- Config First (US #152): privacidade exigida do vídeo vira dado por canal. Ministério (cultos) indexa
-- só vídeo PÚBLICO; Instituto não precisa (já é live_unlisted por regra). NULL = sem restrição extra.
-- =========================================================
ALTER TABLE canal_ingestao ADD COLUMN IF NOT EXISTS privacy_status_filtro TEXT;

ALTER TABLE canal_ingestao DROP CONSTRAINT IF EXISTS canal_ingestao_privacy_status_filtro_check;
ALTER TABLE canal_ingestao ADD CONSTRAINT canal_ingestao_privacy_status_filtro_check
    CHECK (privacy_status_filtro IS NULL OR privacy_status_filtro IN ('public', 'unlisted', 'private'));

UPDATE canal_ingestao SET privacy_status_filtro = 'public' WHERE channel_id = 'UCyHMyZH5oon8jW7y_yMRstQ';

-- Ministério indexa só vídeos cujo título COMEÇA com GIOM (antes: CULTO, MINISTÉRIO ou GIOM*, o que pegava
-- vídeos fora do padrão). Config First: é dado, mudar é um UPDATE.
UPDATE canal_ingestao SET relevancia_valores = ARRAY['GIOM*'] WHERE channel_id = 'UCyHMyZH5oon8jW7y_yMRstQ';
