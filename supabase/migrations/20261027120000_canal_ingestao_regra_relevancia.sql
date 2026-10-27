-- =========================================================
-- Config First (#180): regra de relevância dinâmica por canal. Em vez do enum fixo
-- (relevancia_tipo + relevancia_valores + privacy_status_filtro), o canal guarda uma árvore de
-- condições em JSON (campo / operador / valor, combinadas com todas = E, alguma = OU, nao = NÃO),
-- editável por UPDATE, sem deploy. Formato e campos em docs/regra-relevancia.md.
--
-- Compatível nos dois sentidos: a coluna é opcional. Canal sem regra (NULL) continua pela regra
-- antiga (enum), então código novo + banco antigo e vice-versa funcionam. Os campos antigos ficam
-- como reserva até a regra nova ser a única fonte.
-- =========================================================
ALTER TABLE canal_ingestao ADD COLUMN IF NOT EXISTS regra_relevancia JSONB;

ALTER TABLE canal_ingestao DROP CONSTRAINT IF EXISTS canal_ingestao_regra_relevancia_check;
ALTER TABLE canal_ingestao ADD CONSTRAINT canal_ingestao_regra_relevancia_check
    CHECK (regra_relevancia IS NULL OR jsonb_typeof(regra_relevancia) = 'object');

-- Converte a regra atual de cada canal pro formato novo, a partir do que está na linha (não de valores
-- fixos): live_unlisted = tipo live E privacidade unlisted; titulo_contem = alguma condição de título
-- (valor terminando em "*" = comeca_com, sem "*" = contem); privacy_status_filtro, se houver, entra como
-- E privacidade igual ao filtro. Só preenche canais ainda sem regra, então rodar de novo não sobrescreve
-- uma regra editada.
WITH base AS (
    SELECT channel_id,
           CASE relevancia_tipo::text
               WHEN 'live_unlisted' THEN jsonb_build_object('todas', jsonb_build_array(
                   jsonb_build_object('campo', 'tipo', 'op', 'igual', 'valor', 'live'),
                   jsonb_build_object('campo', 'privacidade', 'op', 'igual', 'valor', 'unlisted')))
               ELSE jsonb_build_object('alguma', COALESCE((
                   SELECT jsonb_agg(CASE
                              WHEN v LIKE '%*' THEN jsonb_build_object('campo', 'titulo', 'op', 'comeca_com', 'valor', left(v, length(v) - 1))
                              ELSE jsonb_build_object('campo', 'titulo', 'op', 'contem', 'valor', v)
                          END)
                   FROM unnest(relevancia_valores) AS v), '[]'::jsonb))
           END AS regra,
           privacy_status_filtro AS pf
    FROM canal_ingestao
    WHERE regra_relevancia IS NULL
)
UPDATE canal_ingestao c
SET regra_relevancia = CASE
        WHEN b.pf IS NULL THEN b.regra
        ELSE jsonb_build_object('todas', jsonb_build_array(
                 b.regra, jsonb_build_object('campo', 'privacidade', 'op', 'igual', 'valor', b.pf)))
    END
FROM base b
WHERE c.channel_id = b.channel_id;
