-- Achado ao vivo no backfill de dev (2026-07-20): a CHAVE trial do Cohere tem um teto adicional,
-- separado do limite documentado do endpoint Embed ("inputs", 2000/min, já coberto pela dimensão
-- 'inputs' desde o Bug #122) — "trial token rate limit exceeded, limit is 100000 tokens per
-- minute". Só aparece em volume (vídeos grandes, muitos chars por chamada), por isso passou
-- despercebido até agora. CohereEmbeddingService passa a pedir também a dimensão 'tokens' (mesmo
-- padrão do Voyage, tokensEstimados = chars/4).
--
-- Dev e prod usam chaves Cohere DIFERENTES (confirmado por hash), mas o mesmo tier (trial) —
-- confirmado com o usuário em 2026-07-20. Seguro deixar essa migration fluir pro release
-- normal dev->master (próximo cutover) em vez de aplicar em prod fora de banda agora: aplicar
-- via apply_migration geraria OUTRO descompasso de timestamp (mesma causa raiz corrigida
-- acima) — melhor deixar o `supabase db push` do pipeline aplicar este arquivo, com o
-- timestamp certo, quando prod receber os commits de dev.
INSERT INTO provedor_rate_limit (provedor, dimensao, capacidade, saldo, taxa_reposicao_por_segundo) VALUES
    ('Cohere', 'tokens', 75000, 75000, 75000.0 / 60) -- 75% de margem sobre 100.000 tokens/min (teto da chave trial)
ON CONFLICT (provedor, dimensao) DO NOTHING;
