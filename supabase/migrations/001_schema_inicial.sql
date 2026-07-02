-- Habilita a extensão pgvector para embeddings
CREATE EXTENSION IF NOT EXISTS vector;

-- =========================================================
-- Tabelas principais
-- =========================================================

CREATE TABLE IF NOT EXISTS video (
    id              TEXT        PRIMARY KEY,
    playlist_id     TEXT,
    channel_id      TEXT,
    channel_title   TEXT,
    title           TEXT,
    description     TEXT,
    length          TEXT,
    privacy_status  TEXT,
    uploaded_at     TIMESTAMPTZ,
    processed_at    TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS transcricao (
    id          SERIAL      PRIMARY KEY,
    video_id    TEXT        NOT NULL REFERENCES video(id) ON DELETE CASCADE,
    conteudo    TEXT,
    formato     TEXT,       -- sbv, srt, vtt, txt
    origem      TEXT,       -- YouTubeCaption, Whisper
    gerado_em   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(video_id)
);

CREATE TABLE IF NOT EXISTS resumo (
    id          SERIAL      PRIMARY KEY,
    video_id    TEXT        NOT NULL REFERENCES video(id) ON DELETE CASCADE,
    descricao   TEXT,
    origem      TEXT,       -- Claude, SummarizeTech
    gerado_em   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(video_id)
);

CREATE TABLE IF NOT EXISTS capitulo (
    id          SERIAL      PRIMARY KEY,
    resumo_id   INT         NOT NULL REFERENCES resumo(id) ON DELETE CASCADE,
    inicio      INTERVAL,
    texto       TEXT
);

CREATE TABLE IF NOT EXISTS categoria_teologica (
    id              SERIAL      PRIMARY KEY,
    video_id        TEXT        NOT NULL REFERENCES video(id) ON DELETE CASCADE,
    area            TEXT,       -- enum AreaTeologica
    subcategoria    TEXT,
    classificado_em TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(video_id)
);

CREATE TABLE IF NOT EXISTS processamento_progresso (
    channel_key         TEXT        NOT NULL,
    playlist_id         TEXT        NOT NULL,
    last_page_token     TEXT,
    last_processed_date TIMESTAMPTZ,
    last_execution_date TIMESTAMPTZ,
    PRIMARY KEY (channel_key, playlist_id)
);

-- =========================================================
-- Tabela de embeddings para RAG (pgvector)
-- 1536 dimensões = text-embedding-3-small da OpenAI
-- =========================================================

CREATE TABLE IF NOT EXISTS video_embedding (
    id          SERIAL          PRIMARY KEY,
    video_id    TEXT            NOT NULL REFERENCES video(id) ON DELETE CASCADE,
    titulo      TEXT,
    conteudo    TEXT,           -- trecho do conteúdo indexado
    embedding   VECTOR(1536),   -- vetor gerado pela OpenAI / Voyage
    indexado_em TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    UNIQUE(video_id)
);

-- Índice HNSW para busca semântica eficiente (melhor que IVFFlat para <1M registros)
CREATE INDEX IF NOT EXISTS idx_video_embedding_hnsw
    ON video_embedding
    USING hnsw (embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);

-- =========================================================
-- Row Level Security — acesso público somente leitura para
-- consultas do agent; escrita apenas com service_role
-- =========================================================

ALTER TABLE video              ENABLE ROW LEVEL SECURITY;
ALTER TABLE transcricao        ENABLE ROW LEVEL SECURITY;
ALTER TABLE resumo             ENABLE ROW LEVEL SECURITY;
ALTER TABLE capitulo           ENABLE ROW LEVEL SECURITY;
ALTER TABLE categoria_teologica ENABLE ROW LEVEL SECURITY;
ALTER TABLE video_embedding    ENABLE ROW LEVEL SECURITY;
ALTER TABLE processamento_progresso ENABLE ROW LEVEL SECURITY;

-- Leitura pública para video, resumo, categoria (o bot precisa)
CREATE POLICY "leitura_publica_video"        ON video              FOR SELECT USING (true);
CREATE POLICY "leitura_publica_resumo"       ON resumo             FOR SELECT USING (true);
CREATE POLICY "leitura_publica_categoria"    ON categoria_teologica FOR SELECT USING (true);
CREATE POLICY "leitura_publica_embedding"    ON video_embedding    FOR SELECT USING (true);

-- Escrita apenas service_role (o worker C# usa service_role_key)
CREATE POLICY "escrita_service_role_video"       ON video              FOR ALL  USING (auth.role() = 'service_role');
CREATE POLICY "escrita_service_role_transcricao" ON transcricao        FOR ALL  USING (auth.role() = 'service_role');
CREATE POLICY "escrita_service_role_resumo"      ON resumo             FOR ALL  USING (auth.role() = 'service_role');
CREATE POLICY "escrita_service_role_capitulo"    ON capitulo           FOR ALL  USING (auth.role() = 'service_role');
CREATE POLICY "escrita_service_role_categoria"   ON categoria_teologica FOR ALL USING (auth.role() = 'service_role');
CREATE POLICY "escrita_service_role_embedding"   ON video_embedding    FOR ALL  USING (auth.role() = 'service_role');
CREATE POLICY "escrita_service_role_progresso"   ON processamento_progresso FOR ALL USING (auth.role() = 'service_role');
