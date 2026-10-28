-- DOWN de 20261027130000: a regra volta a ser opcional e o enum antigo volta a ser obrigatório. Reverter o
-- código antes (a versão antiga lê relevancia_tipo / relevancia_valores / privacy_status_filtro).
ALTER TABLE canal_ingestao ALTER COLUMN regra_relevancia DROP NOT NULL;

UPDATE canal_ingestao SET relevancia_tipo = 'titulo_contem' WHERE relevancia_tipo IS NULL;
ALTER TABLE canal_ingestao ALTER COLUMN relevancia_tipo SET NOT NULL;

COMMENT ON COLUMN canal_ingestao.regra_relevancia IS NULL;
COMMENT ON COLUMN canal_ingestao.relevancia_tipo IS NULL;
COMMENT ON COLUMN canal_ingestao.relevancia_valores IS NULL;
COMMENT ON COLUMN canal_ingestao.privacy_status_filtro IS NULL;
