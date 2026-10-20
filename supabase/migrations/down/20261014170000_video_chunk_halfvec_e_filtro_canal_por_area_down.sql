-- =========================================================
-- DOWN de supabase/migrations/20261014170000_video_chunk_halfvec_e_filtro_canal_por_area.sql
--
-- Rollback MANUAL (psql / SQL editor do Supabase) — fica fora do nível raiz de
-- supabase/migrations/ justamente pra o `supabase db push` não aplicar. Rodar isto NÃO remove a
-- linha 20261014170000 de supabase_migrations.schema_migrations; se o arquivo UP também for
-- removido/renomeado do repo, ajustar o histórico (ver docs/ENGINEERING_STANDARDS.md#migrations).
--
-- Reverte video_chunk.embedding pra vector(1536) + HNSW vector_cosine_ops (m=16,
-- ef_construction=64) e restaura as 4 RPCs comparando direto com query_embedding (sem cast).
-- Os valores voltam com a precisão do float16 — o float32 original não é recuperável.
--
-- Escolha: match_chunks_hibrido_por_area MANTÉM o fix do canal_id_filtro (JOIN video + filtro
-- nas duas CTEs). Voltar ao comportamento anterior reintroduziria o vazamento de chunks de
-- outros canais na busca por área, e o fix não depende do tipo da coluna.
-- =========================================================

BEGIN;

DROP INDEX IF EXISTS idx_video_chunk_hnsw;

ALTER TABLE video_chunk
    ALTER COLUMN embedding TYPE vector(1536) USING embedding::vector(1536);

CREATE INDEX idx_video_chunk_hnsw
    ON video_chunk
    USING hnsw (embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);

CREATE OR REPLACE FUNCTION match_chunks(
    query_embedding  VECTOR,
    match_threshold  FLOAT   DEFAULT 0.35,
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
    LEFT JOIN LATERAL (
        SELECT c.area, c.subcategoria FROM categoria_teologica c
        WHERE c.video_id = vc.video_id ORDER BY c.peso DESC LIMIT 1
    ) cat ON true
    WHERE 1 - (vc.embedding <=> query_embedding) > match_threshold
    ORDER BY vc.embedding <=> query_embedding
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks(vector, double precision, integer) TO anon, authenticated;

CREATE OR REPLACE FUNCTION match_chunks_por_area(
    query_embedding  VECTOR,
    area_filtro      TEXT,
    match_threshold  FLOAT   DEFAULT 0.30,
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
    INNER JOIN LATERAL (
        SELECT c.area, c.subcategoria, c.peso FROM categoria_teologica c
        WHERE c.video_id = vc.video_id AND c.area ILIKE area_filtro
        ORDER BY c.peso DESC LIMIT 1
    ) cat ON true
    WHERE 1 - (vc.embedding <=> query_embedding) > match_threshold
    ORDER BY vc.embedding <=> query_embedding
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks_por_area(vector, text, double precision, integer) TO anon, authenticated;

CREATE OR REPLACE FUNCTION match_chunks_hibrido(
    query_embedding   VECTOR,
    query_texto       TEXT,
    origem_filtro     TEXT    DEFAULT 'OpenAI',
    match_count       INT     DEFAULT 6,
    candidatos        INT     DEFAULT 30,
    canal_id_filtro   TEXT    DEFAULT NULL
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
    WITH semantica AS (
        SELECT vc.id, ROW_NUMBER() OVER (ORDER BY vc.embedding <=> query_embedding) AS rank_sem
        FROM video_chunk vc
        JOIN video v ON v.id = vc.video_id
        WHERE vc.origem = origem_filtro
          AND (canal_id_filtro IS NULL OR v.channel_id = canal_id_filtro)
        ORDER BY vc.embedding <=> query_embedding
        LIMIT candidatos
    ),
    textual AS (
        SELECT vc.id,
               ROW_NUMBER() OVER (ORDER BY ts_rank(vc.conteudo_tsv, websearch_to_tsquery('portuguese', query_texto)) DESC) AS rank_txt
        FROM video_chunk vc
        JOIN video v ON v.id = vc.video_id
        WHERE vc.origem = origem_filtro
          AND vc.conteudo_tsv @@ websearch_to_tsquery('portuguese', query_texto)
          AND (canal_id_filtro IS NULL OR v.channel_id = canal_id_filtro)
        LIMIT candidatos
    ),
    combinado AS (
        SELECT COALESCE(s.id, t.id) AS id,
               COALESCE(1.0 / (60 + s.rank_sem), 0) + COALESCE(1.0 / (60 + t.rank_txt), 0) AS rrf_score
        FROM semantica s
        FULL OUTER JOIN textual t ON s.id = t.id
    )
    SELECT vc.video_id, vc.titulo, vc.chunk_index, vc.conteudo, cat.area, cat.subcategoria,
           1 - (vc.embedding <=> query_embedding) AS similarity
    FROM combinado c
    JOIN video_chunk vc ON vc.id = c.id
    LEFT JOIN LATERAL (
        SELECT ct.area, ct.subcategoria FROM categoria_teologica ct
        WHERE ct.video_id = vc.video_id ORDER BY ct.peso DESC LIMIT 1
    ) cat ON true
    ORDER BY c.rrf_score DESC
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks_hibrido(vector, text, text, integer, integer, text) TO anon, authenticated;

CREATE OR REPLACE FUNCTION match_chunks_hibrido_por_area(
    query_embedding   VECTOR,
    query_texto       TEXT,
    area_filtro       TEXT,
    origem_filtro     TEXT    DEFAULT 'OpenAI',
    match_count       INT     DEFAULT 6,
    candidatos        INT     DEFAULT 30,
    canal_id_filtro   TEXT    DEFAULT NULL
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
    WITH semantica AS (
        SELECT vc.id, ROW_NUMBER() OVER (ORDER BY vc.embedding <=> query_embedding) AS rank_sem
        FROM video_chunk vc
        JOIN video v ON v.id = vc.video_id
        WHERE vc.origem = origem_filtro
          AND EXISTS (SELECT 1 FROM categoria_teologica cc WHERE cc.video_id = vc.video_id AND cc.area ILIKE area_filtro)
          AND (canal_id_filtro IS NULL OR v.channel_id = canal_id_filtro)
        ORDER BY vc.embedding <=> query_embedding
        LIMIT candidatos
    ),
    textual AS (
        SELECT vc.id,
               ROW_NUMBER() OVER (ORDER BY ts_rank(vc.conteudo_tsv, websearch_to_tsquery('portuguese', query_texto)) DESC) AS rank_txt
        FROM video_chunk vc
        JOIN video v ON v.id = vc.video_id
        WHERE vc.origem = origem_filtro
          AND EXISTS (SELECT 1 FROM categoria_teologica cc WHERE cc.video_id = vc.video_id AND cc.area ILIKE area_filtro)
          AND vc.conteudo_tsv @@ websearch_to_tsquery('portuguese', query_texto)
          AND (canal_id_filtro IS NULL OR v.channel_id = canal_id_filtro)
        LIMIT candidatos
    ),
    combinado AS (
        SELECT COALESCE(s.id, t.id) AS id,
               COALESCE(1.0 / (60 + s.rank_sem), 0) + COALESCE(1.0 / (60 + t.rank_txt), 0) AS rrf_score
        FROM semantica s
        FULL OUTER JOIN textual t ON s.id = t.id
    )
    SELECT vc.video_id, vc.titulo, vc.chunk_index, vc.conteudo, cat.area, cat.subcategoria,
           1 - (vc.embedding <=> query_embedding) AS similarity
    FROM combinado c
    JOIN video_chunk vc ON vc.id = c.id
    LEFT JOIN LATERAL (
        SELECT ct.area, ct.subcategoria FROM categoria_teologica ct
        WHERE ct.video_id = vc.video_id ORDER BY ct.peso DESC LIMIT 1
    ) cat ON true
    ORDER BY c.rrf_score DESC
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks_hibrido_por_area(vector, text, text, text, integer, integer, text) TO anon, authenticated;

COMMIT;
