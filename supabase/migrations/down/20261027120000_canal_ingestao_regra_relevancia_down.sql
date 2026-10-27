-- DOWN de 20261027120000: remove a regra de relevância dinâmica. Os canais voltam a usar o enum
-- (relevancia_tipo + relevancia_valores + privacy_status_filtro), que continua preenchido.
ALTER TABLE canal_ingestao DROP CONSTRAINT IF EXISTS canal_ingestao_regra_relevancia_check;
ALTER TABLE canal_ingestao DROP COLUMN IF EXISTS regra_relevancia;
