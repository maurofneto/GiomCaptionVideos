-- =========================================================
-- Registro de canais ativos pra ingestão (US #105) — substitui os ChannelConfig[]
-- hardcoded em Instituto.Worker/Ministerio.Worker por uma linha por canal, lida no
-- boot do Ingestao.Worker. secrets_key referencia o path no SSM
-- (/giom/{env}/canais/{secrets_key}/Google/...) — NUNCA guarda o segredo aqui (ver
-- CLAUDE.md: secret sempre em SSM, nunca em banco/git).
-- =========================================================

CREATE TYPE relevancia_tipo AS ENUM ('live_unlisted', 'titulo_contem');

CREATE TABLE IF NOT EXISTS canal_ingestao (
    channel_id              TEXT PRIMARY KEY,           -- YouTube channel id (ChannelConfig.ChannelId)
    playlist_id             TEXT,                        -- nullable, igual ChannelConfig.PlaylistId
    nome                     TEXT            NOT NULL,    -- ChannelConfig.Name
    mine                     BOOLEAN         NOT NULL DEFAULT false,
    ativo                    BOOLEAN         NOT NULL DEFAULT true,
    relevancia_tipo          relevancia_tipo NOT NULL,
    -- Convenção pra relevancia_tipo = 'titulo_contem': valor terminando em "*" = prefixo
    -- (StartsWith), sem "*" = Contains. Evita um 3º tipo de enum só pra essa mistura
    -- (regra original do Ministério mistura "contém CULTO/MINISTÉRIO" com "começa com GIOM").
    relevancia_valores       TEXT[]          NOT NULL DEFAULT '{}',
    max_videos_per_run       INT             NOT NULL DEFAULT 20,
    delay_between_videos_ms  INT             NOT NULL DEFAULT 3000,
    delay_between_pages_ms   INT             NOT NULL DEFAULT 1200,
    secrets_key              TEXT            NOT NULL,    -- slug: "instituto" | "ministerio" | novo
    created_at               TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at               TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

ALTER TABLE canal_ingestao ENABLE ROW LEVEL SECURITY;

-- Igual a app_secret — é config operacional, não dado de produto, só o worker (service_role)
-- lê/escreve, sem exposição a anon/authenticated.
CREATE POLICY "service_role_only_canal_ingestao" ON canal_ingestao FOR ALL USING (auth.role() = 'service_role');

-- Seed dos 2 canais existentes hoje — dado real (channel_id, nome), não secret, ok versionar.
INSERT INTO canal_ingestao (channel_id, playlist_id, nome, mine, relevancia_tipo, relevancia_valores,
                             max_videos_per_run, delay_between_videos_ms, delay_between_pages_ms, secrets_key)
VALUES
    ('UCyyXFx2xHdzfavx6H6Q8zJw', NULL, 'Instituto Giom', true,
     'live_unlisted', '{}', 20, 5000, 1200, 'instituto'),
    ('UCyHMyZH5oon8jW7y_yMRstQ', NULL, 'Ministério Giom', false,
     'titulo_contem', ARRAY['CULTO', 'MINISTÉRIO', 'GIOM*'], 35, 3000, 1200, 'ministerio')
ON CONFLICT (channel_id) DO NOTHING;
