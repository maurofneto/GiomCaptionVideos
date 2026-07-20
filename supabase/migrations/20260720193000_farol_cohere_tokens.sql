-- Achado ao vivo no backfill de dev (2026-07-20): a CHAVE trial do Cohere tem um teto adicional,
-- separado do limite documentado do endpoint Embed ("inputs", 2000/min, já coberto pela dimensão
-- 'inputs' desde o Bug #122) — "trial token rate limit exceeded, limit is 100000 tokens per
-- minute". Só aparece em volume (vídeos grandes, muitos chars por chamada), por isso passou
-- despercebido até agora. CohereEmbeddingService passa a pedir também a dimensão 'tokens' (mesmo
-- padrão do Voyage, tokensEstimados = chars/4).
--
-- Aplicado só em dev por enquanto: dev e prod usam chaves Cohere DIFERENTES (confirmado por
-- hash), e não se sabe se a chave de prod também é trial — não presumir o mesmo teto lá sem
-- confirmar o tier da conta.
INSERT INTO provedor_rate_limit (provedor, dimensao, capacidade, saldo, taxa_reposicao_por_segundo) VALUES
    ('Cohere', 'tokens', 75000, 75000, 75000.0 / 60) -- 75% de margem sobre 100.000 tokens/min (teto da chave trial)
ON CONFLICT (provedor, dimensao) DO NOTHING;
