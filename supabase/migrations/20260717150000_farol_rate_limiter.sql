-- =========================================================
-- Bug #122: 3 tasks concorrentes do mesmo Service (giom-embedding-{dev,prod}-service,
-- autoscaling até maxCapacity=3) disparavam requisições simultâneas contra o mesmo limite de
-- rate do provedor externo (Voyage/Cohere), estourando 429 mesmo com o retry exponencial do
-- Polly (Standard-Retry) — o volume concorrente continuava alto durante toda a janela de retry.
--
-- Cada task é um processo ECS separado, sem estado compartilhado entre si — um rate limiter
-- in-memory (ex: SemaphoreSlim) não coordenaria as 3 tasks. O farol resolve isso sem infra
-- nova: o Postgres (já usado por tudo mais no pipeline) vira o "lugar único" que TODAS as
-- tasks concorrentes enxergam, via um token bucket clássico por provedor.
--
-- Dimensões (por que RPM e TPM separados, não um bucket genérico único): o próprio provedor
-- aplica os dois limites AO MESMO TEMPO e de forma independente — a resposta 429 do Voyage
-- capturada no Bug #122 dizia literalmente "3 RPM and 10K TPM". Forçar isso num único bucket
-- "tokens" (fazendo de conta que 1 requisição = 1 token) escondia qual dos dois limites
-- realmente disparou, e não protegia contra o outro. Por isso PROVEDOR+DIMENSAO é a chave: cada
-- dimensão (requisicoes, tokens) tem sua própria capacidade e taxa de reposição, e o consumo é
-- tudo-ou-nada entre as dimensões pedidas numa mesma chamada (nunca debita só uma e fica preso
-- esperando a outra com o crédito já gasto).
-- =========================================================

CREATE TABLE IF NOT EXISTS provedor_rate_limit (
    provedor                TEXT        NOT NULL,
    dimensao                TEXT        NOT NULL, -- 'requisicoes' (RPM) ou 'tokens' (TPM) — nomes explícitos, nunca conflitados num bucket genérico
    capacidade               NUMERIC     NOT NULL,
    saldo                    NUMERIC     NOT NULL,
    taxa_reposicao_por_segundo NUMERIC   NOT NULL,
    atualizado_em            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (provedor, dimensao)
);

COMMENT ON TABLE provedor_rate_limit IS 'Farol de rate limit (Bug #122) — token bucket por provedor+dimensão (requisicoes/tokens), compartilhado entre todas as tasks concorrentes do mesmo Service via Postgres.';

ALTER TABLE provedor_rate_limit ENABLE ROW LEVEL SECURITY;

-- Igual a canal_ingestao/etapa_provedor_config — dado operacional, só o worker (service_role) lê/escreve.
CREATE POLICY "service_role_only_provedor_rate_limit" ON provedor_rate_limit
    FOR ALL USING (auth.role() = 'service_role');

-- Consome `p_consumo` (ex: {"requisicoes": 1, "tokens": 4200}) do provedor `p_provedor`,
-- refletindo o refill decorrido desde a última consulta em CADA dimensão pedida. Concede
-- (debita e retorna true) só se TODAS as dimensões tiverem saldo suficiente — tudo-ou-nada, pra
-- nunca gastar o crédito de uma dimensão enquanto espera outra. Se negado, ainda persiste o
-- refill parcial calculado (não perde o tempo decorrido, só não debita nada).
--
-- `SELECT ... ORDER BY dimensao FOR UPDATE` trava as linhas envolvidas ANTES de qualquer leitura,
-- em ordem estável — evita deadlock entre duas chamadas concorrentes pedindo as mesmas dimensões
-- em ordens diferentes. Uma única chamada RPC via PostgREST = uma única transação, então o lock
-- persiste até o fim da função: duas tasks concorrentes nunca leem/decrementam o mesmo saldo em
-- paralelo, sem precisar de nenhum lock do lado do worker.
CREATE OR REPLACE FUNCTION farol_consumir(p_provedor TEXT, p_consumo JSONB)
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
    v_dimensao TEXT;
    v_quantidade NUMERIC;
    v_saldo_disponivel NUMERIC;
    v_saldos_disponiveis JSONB := '{}'::jsonb;
    v_pode_conceder BOOLEAN := TRUE;
BEGIN
    -- Trava todas as linhas envolvidas de uma vez, em ordem estável (dimensao).
    PERFORM 1
    FROM provedor_rate_limit
    WHERE provedor = p_provedor AND dimensao IN (SELECT jsonb_object_keys(p_consumo))
    ORDER BY dimensao
    FOR UPDATE;

    FOR v_dimensao, v_quantidade IN SELECT key, value::numeric FROM jsonb_each_text(p_consumo)
    LOOP
        SELECT LEAST(capacidade, saldo + taxa_reposicao_por_segundo * EXTRACT(EPOCH FROM (NOW() - atualizado_em)))
        INTO v_saldo_disponivel
        FROM provedor_rate_limit
        WHERE provedor = p_provedor AND dimensao = v_dimensao;

        IF v_saldo_disponivel IS NULL THEN
            RAISE EXCEPTION 'Provedor %/% não cadastrado em provedor_rate_limit', p_provedor, v_dimensao;
        END IF;

        v_saldos_disponiveis := v_saldos_disponiveis || jsonb_build_object(v_dimensao, v_saldo_disponivel);

        IF v_saldo_disponivel < v_quantidade THEN
            v_pode_conceder := FALSE;
        END IF;
    END LOOP;

    FOR v_dimensao, v_quantidade IN SELECT key, value::numeric FROM jsonb_each_text(p_consumo)
    LOOP
        UPDATE provedor_rate_limit
        SET saldo = (v_saldos_disponiveis ->> v_dimensao)::numeric - (CASE WHEN v_pode_conceder THEN v_quantidade ELSE 0 END),
            atualizado_em = NOW()
        WHERE provedor = p_provedor AND dimensao = v_dimensao;
    END LOOP;

    RETURN v_pode_conceder;
END;
$$;

-- Seed inicial — a partir da documentação oficial de cada provedor (não mais estimativa), 25%
-- de margem de segurança sobre o limite documentado (ex: cliente HTTP concorrente com outro
-- processo, relógio ligeiramente dessincronizado entre tasks). Ajustável depois via UPDATE
-- direto na tabela (sem redeploy), mesma convenção operacional de canal_ingestao/
-- etapa_provedor_config.
--
--   VoyageAI (docs.voyageai.com/docs/rate-limits): Tier 1 (pagamento cadastrado nesta sessão) —
--             2000 RPM / 3M TPM pra voyage-3-large/voyage-code-3/série 1&2 (bucket onde
--             "voyage-3", o modelo hoje configurado em VoyageEmbeddingOptions.Model, mais se
--             aproxima — CONFERIR no dashboard qual bucket exato o console mostra pra esse
--             modelo específico, a tabela oficial não lista "voyage-3" por nome). Tier 2 (≥$100
--             pago) dobra, Tier 3 (≥$1000) triplica — reajustar aqui quando a conta subir de
--             tier (não é automático).
--   Cohere (docs.cohere.com/docs/rate-limits): Embed (o único endpoint usado aqui) limita por
--             "inputs" (nº de textos no batch), não por request nem por token — 2000
--             inputs/min, MESMO valor pra trial e produção. Por isso a dimensão é 'inputs', não
--             'tokens' — CohereEmbeddingService consome nessa dimensão (ver Bug #122).
--   OpenAI:   sem número fixo aqui de propósito — os limites reais (por tier de gasto da conta)
--             vêm nos headers de toda resposta (x-ratelimit-limit-requests/-tokens) ou em
--             platform.openai.com/settings/organization/limits. Nunca observamos 429 no
--             histórico desta pipeline, então o valor abaixo é só um teto de segurança bem
--             folgado (Tier 1 típico), não um número copiado de tabela oficial — ajustar se/quando
--             tivermos o valor real da conta.
INSERT INTO provedor_rate_limit (provedor, dimensao, capacidade, saldo, taxa_reposicao_por_segundo) VALUES
    ('VoyageAI', 'requisicoes', 1500,    1500,    1500.0    / 60), -- 75% de 2000 RPM (Tier 1)
    ('VoyageAI', 'tokens',      2250000, 2250000, 2250000.0 / 60), -- 75% de 3M TPM (Tier 1)
    ('Cohere',   'inputs',      1500,    1500,    1500.0    / 60), -- 75% de 2000 inputs/min
    ('OpenAI',   'requisicoes', 3000,    3000,    3000.0    / 60), -- teto de segurança, não da tabela oficial (ver comentário acima)
    ('OpenAI',   'tokens',      1000000, 1000000, 1000000.0 / 60) -- idem
ON CONFLICT (provedor, dimensao) DO NOTHING;
