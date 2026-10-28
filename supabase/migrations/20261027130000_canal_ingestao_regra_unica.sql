-- =========================================================
-- Config First (#180): a regra dinâmica passa a ser a ÚNICA definição de relevância do canal. O código
-- deixa de ler relevancia_tipo / relevancia_valores / privacy_status_filtro e não há mais regra antiga pra
-- cair: canal sem regra (ou com regra inválida) não processa nenhum vídeo e gera alerta.
--
-- Ordem de deploy: aplicar esta migration (e a 20261027120000) ANTES do código novo. Com o código novo e
-- sem a coluna regra_relevancia, a ingestão não processa vídeo nenhum até a migration entrar.
--
-- As colunas antigas ficam, só sem uso, até a regra nova ser validada em produção; uma migration seguinte
-- as remove (apagar coluna não volta).
-- =========================================================

-- Canal que por algum motivo ainda não tem regra fica explicitamente sem nenhum vídeo relevante, em vez de
-- impedir o NOT NULL. (A 20261027120000 já converteu os canais existentes a partir do enum.)
UPDATE canal_ingestao SET regra_relevancia = '{"alguma": []}'::jsonb WHERE regra_relevancia IS NULL;

ALTER TABLE canal_ingestao ALTER COLUMN regra_relevancia SET NOT NULL;

-- Canal novo não precisa mais preencher o enum antigo.
ALTER TABLE canal_ingestao ALTER COLUMN relevancia_tipo DROP NOT NULL;

COMMENT ON COLUMN canal_ingestao.regra_relevancia IS
    'Regra de relevância do canal (#180), árvore JSON de condições. Formato em docs/regra-relevancia.md.';
COMMENT ON COLUMN canal_ingestao.relevancia_tipo IS 'OBSOLETA (#180): substituída por regra_relevancia; o código não lê mais.';
COMMENT ON COLUMN canal_ingestao.relevancia_valores IS 'OBSOLETA (#180): substituída por regra_relevancia; o código não lê mais.';
COMMENT ON COLUMN canal_ingestao.privacy_status_filtro IS 'OBSOLETA (#180): substituída por regra_relevancia; o código não lê mais.';
