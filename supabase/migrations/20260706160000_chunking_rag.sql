-- =========================================================
-- Chunking RAG para o Instituto Giom
--
-- Substitui video_embedding (1 por vídeo) por video_chunk
-- (N por vídeo, cada um representando ~600 tokens da transcrição).
--
-- Com ~100 vídeos × ~50 chunks = ~5.000 embeddings no total.
-- =========================================================

-- Depreca a tabela antiga (mantém para não quebrar nada)
ALTER TABLE IF EXISTS video_embedding RENAME TO video_embedding_deprecated;

-- Nova tabela de chunks
CREATE TABLE IF NOT EXISTS video_chunk (
    id              SERIAL          PRIMARY KEY,
    video_id        TEXT            NOT NULL REFERENCES video(id) ON DELETE CASCADE,
    titulo          TEXT,
    chunk_index     INT             NOT NULL,   -- posição do chunk dentro do vídeo (0-based)
    conteudo        TEXT            NOT NULL,   -- ~600 tokens do texto da transcrição
    embedding       VECTOR(1536),
    indexado_em     TIMESTAMPTZ     NOT NULL DEFAULT NOW(),

    UNIQUE (video_id, chunk_index)
);

-- Índice HNSW para busca semântica eficiente nos chunks
CREATE INDEX IF NOT EXISTS idx_video_chunk_hnsw
    ON video_chunk
    USING hnsw (embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);

-- RLS
ALTER TABLE video_chunk ENABLE ROW LEVEL SECURITY;

CREATE POLICY "leitura_publica_chunk"       ON video_chunk FOR SELECT USING (true);
CREATE POLICY "escrita_service_role_chunk"  ON video_chunk FOR ALL    USING (auth.role() = 'service_role');

-- =========================================================
-- RPC: busca semântica nos chunks
--
-- Retorna os N chunks mais próximos (de vídeos diferentes
-- ou do mesmo vídeo) com metadados do vídeo pai.
-- =========================================================

CREATE OR REPLACE FUNCTION match_chunks(
    query_embedding  VECTOR(1536),
    match_threshold  FLOAT   DEFAULT 0.70,
    match_count      INT     DEFAULT 6
)
RETURNS TABLE (
    video_id        TEXT,
    titulo          TEXT,
    chunk_index     INT,
    conteudo        TEXT,
    area            TEXT,
    subcategoria    TEXT,
    similarity      FLOAT
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        vc.video_id,
        vc.titulo,
        vc.chunk_index,
        vc.conteudo,
        cat.area,
        cat.subcategoria,
        1 - (vc.embedding <=> query_embedding) AS similarity
    FROM video_chunk vc
    LEFT JOIN categoria_teologica cat ON cat.video_id = vc.video_id
    WHERE 1 - (vc.embedding <=> query_embedding) > match_threshold
    ORDER BY vc.embedding <=> query_embedding
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks TO anon, authenticated;

-- =========================================================
-- RPC: busca filtrada por área teológica
-- =========================================================

CREATE OR REPLACE FUNCTION match_chunks_por_area(
    query_embedding  VECTOR(1536),
    area_filtro      TEXT,
    match_threshold  FLOAT   DEFAULT 0.65,
    match_count      INT     DEFAULT 6
)
RETURNS TABLE (
    video_id        TEXT,
    titulo          TEXT,
    chunk_index     INT,
    conteudo        TEXT,
    area            TEXT,
    subcategoria    TEXT,
    similarity      FLOAT
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        vc.video_id,
        vc.titulo,
        vc.chunk_index,
        vc.conteudo,
        cat.area,
        cat.subcategoria,
        1 - (vc.embedding <=> query_embedding) AS similarity
    FROM video_chunk vc
    INNER JOIN categoria_teologica cat ON cat.video_id = vc.video_id
    WHERE cat.area ILIKE area_filtro
      AND 1 - (vc.embedding <=> query_embedding) > match_threshold
    ORDER BY vc.embedding <=> query_embedding
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks_por_area TO anon, authenticated;
