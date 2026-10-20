-- DOWN de 20261019130000: remove a coluna (reverter o código antes — o repositório lê privacy_status_filtro).
ALTER TABLE canal_ingestao DROP COLUMN IF EXISTS privacy_status_filtro;
