-- =========================================================
-- Preço por 1M tokens de cada (provedor, modelo) usado no pipeline (Task #130) — antes vivia
-- num dicionário estático em código (PricingCalculator.cs), que exigia deploy só pra corrigir um
-- número (ex: promoção com prazo, modelo novo). Mesma ideia de etapa_provedor_config: dado
-- operacional que muda, mora no banco, não no binário.
-- Comparação provedor/modelo é case-insensitive (era assim no dicionário original, via
-- IEqualityComparer dedicado) — o repositório (Infrastructure.Supabase) usa `ilike` em vez de
-- `eq` pra preservar esse comportamento sem precisar normalizar o valor armazenado.
-- saida_por_milhao = 0 pra embeddings (só têm 1 tipo de token; quem chama passa tokensSaida: null).
-- =========================================================

CREATE TABLE IF NOT EXISTS provedor_preco (
    provedor              TEXT NOT NULL,
    modelo                TEXT NOT NULL,
    entrada_por_milhao    NUMERIC(10,4) NOT NULL,
    saida_por_milhao      NUMERIC(10,4) NOT NULL DEFAULT 0,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (provedor, modelo)
);

ALTER TABLE provedor_preco ENABLE ROW LEVEL SECURITY;

-- Igual a etapa_provedor_config/canal_ingestao/app_secret — config operacional, só o worker
-- (service_role) lê/escreve.
CREATE POLICY "service_role_only_provedor_preco" ON provedor_preco
    FOR ALL USING (auth.role() = 'service_role');

-- Seed: valores idênticos aos hoje hardcoded em PricingCalculator.cs — troca só a origem
-- (código -> banco), sem mudar nenhum preço. Comentários de proveniência preservados.
INSERT INTO provedor_preco (provedor, modelo, entrada_por_milhao, saida_por_milhao) VALUES
-- Claude (chat) — Sonnet 5 em preço promocional até 2026-08-31 (US$2,00/US$10,00 por 1M).
('Claude', 'claude-sonnet-5', 2.00, 10.00),
('Claude', 'claude-haiku-4-5-20251001', 1.00, 5.00),
('Claude', 'claude-haiku-4-5', 1.00, 5.00),

-- OpenAI (embeddings)
('OpenAI', 'text-embedding-3-small', 0.02, 0),
('OpenAI', 'text-embedding-3-large', 0.13, 0),

-- OpenAI (chat, Resumo/Categorização)
('OpenAI', 'gpt-4o-mini', 0.15, 0.60),
('OpenAI', 'gpt-4o', 2.50, 10.00),

-- VoyageAI (embeddings) — cota gratuita de 200M tokens na criação da conta NÃO descontada
-- aqui, é o custo de tabela cheio (ver Bug #118/#122).
('VoyageAI', 'voyage-3-lite', 0.02, 0),
('VoyageAI', 'voyage-3', 0.06, 0),
('VoyageAI', 'voyage-3-large', 0.18, 0),
('VoyageAI', 'voyage-code-3', 0.18, 0),

-- Cohere (embeddings)
('Cohere', 'embed-v4.0', 0.12, 0),
('Cohere', 'embed-english-v3.0', 0.10, 0),
('Cohere', 'embed-multilingual-v3.0', 0.10, 0),

-- Grok/xAI (chat)
('Grok', 'grok-3-mini', 0.30, 0.50),
('Grok', 'grok-3', 3.00, 15.00),

-- Kimi/Moonshot (chat, US #124) — US$3,00 entrada (cache miss — cache hit é US$0,30, não
-- modelado aqui, custo de tabela cheio) / US$15,00 saída por 1M tokens.
('Kimi', 'kimi-k3', 3.00, 15.00)
ON CONFLICT (provedor, modelo) DO NOTHING;
