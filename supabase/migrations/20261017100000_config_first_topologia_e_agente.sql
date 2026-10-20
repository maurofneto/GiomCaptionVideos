-- =========================================================
-- Config First (US #152): topologia do pipeline e instrução de concisão do bot saem do
-- appsettings e passam a viver no banco — mudar o comportamento vira um INSERT/UPDATE, sem
-- deploy. Ver docs/CONFIG_FIRST.md.
--
-- etapa_topologia: uma linha por (canal, etapa) com as PRÓXIMAS etapas a publicar. proximas vazio
-- = etapa terminal (marca ProcessedAt quando todos os ramos terminais concluírem, bug #111).
-- Mesmo formato que Topologia:Topologia tinha no appsettings do Orquestracao.Worker.
--
-- agente_config: instrução de tamanho de resposta do bot por provedor ('*' = todos). Vazio = o
-- system prompt (agent.md) segue inalterado.
-- =========================================================

CREATE TABLE IF NOT EXISTS etapa_topologia (
    channel_id  TEXT   NOT NULL REFERENCES canal_ingestao(channel_id),
    etapa       TEXT   NOT NULL CHECK (etapa IN ('transcricao', 'resumo', 'categorizacao', 'embedding')),
    proximas    TEXT[] NOT NULL DEFAULT '{}'
                CHECK (proximas <@ ARRAY['resumo', 'categorizacao', 'embedding']::TEXT[]),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (channel_id, etapa)
);

ALTER TABLE etapa_topologia ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS "service_role_only_etapa_topologia" ON etapa_topologia;
CREATE POLICY "service_role_only_etapa_topologia" ON etapa_topologia
    FOR ALL USING (auth.role() = 'service_role');

-- Seed = a topologia que estava no appsettings (Instituto inalterado). Ministério passa a ter
-- embedding em paralelo ao resumo (US #151); a cadeia de provedores vem de etapa_provedor_config.
INSERT INTO etapa_topologia (channel_id, etapa, proximas) VALUES
    ('UCyyXFx2xHdzfavx6H6Q8zJw', 'transcricao',   ARRAY['resumo', 'embedding']),
    ('UCyyXFx2xHdzfavx6H6Q8zJw', 'resumo',        ARRAY['categorizacao']),
    ('UCyyXFx2xHdzfavx6H6Q8zJw', 'categorizacao', ARRAY[]::TEXT[]),
    ('UCyyXFx2xHdzfavx6H6Q8zJw', 'embedding',     ARRAY[]::TEXT[]),
    ('UCyHMyZH5oon8jW7y_yMRstQ', 'transcricao',   ARRAY['resumo', 'embedding']),
    ('UCyHMyZH5oon8jW7y_yMRstQ', 'resumo',        ARRAY[]::TEXT[]),
    ('UCyHMyZH5oon8jW7y_yMRstQ', 'embedding',     ARRAY[]::TEXT[])
ON CONFLICT (channel_id, etapa) DO NOTHING;

CREATE TABLE IF NOT EXISTS agente_config (
    provedor            TEXT PRIMARY KEY,
    instrucao_concisao  TEXT NOT NULL DEFAULT '',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE agente_config ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS "service_role_only_agente_config" ON agente_config;
CREATE POLICY "service_role_only_agente_config" ON agente_config
    FOR ALL USING (auth.role() = 'service_role');

INSERT INTO agente_config (provedor, instrucao_concisao) VALUES
    ('*', 'Responda em no máximo 3 parágrafos curtos (cerca de 150 palavras). Aprofunde só se o aluno pedir mais detalhes.')
ON CONFLICT (provedor) DO NOTHING;
