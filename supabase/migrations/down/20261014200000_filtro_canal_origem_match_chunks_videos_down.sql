-- =========================================================
-- Qualquer DDL pesado: rodar com SET statement_timeout = 0 e SET maintenance_work_mem = '64MB' (no máximo; 256MB falhou por falta de memória compartilhada no prod)
--
-- DOWN de 20261014200000_filtro_canal_origem_match_chunks_videos.sql: remove as assinaturas com
-- canal_id_filtro/origem_filtro e recria as 4 RPCs exatamente como estavam em prod antes dela.
--
-- ATENÇÃO: match_videos e match_videos_por_area voltam a ler video_embedding, tabela que não
-- existe desde 20260706160000_chunking_rag.sql (renomeada pra video_embedding_deprecated) —
-- ou seja, voltam QUEBRADAS (erro "relation video_embedding does not exist" ao chamar), como
-- estavam em prod. plpgsql não valida tabelas no CREATE, então o DOWN em si aplica sem erro.
--
-- Só funções (sem build de índice) — basta o statement_timeout.
-- =========================================================

BEGIN;

SET LOCAL statement_timeout = 0;

DROP FUNCTION IF EXISTS match_chunks(vector, double precision, integer, text, text);
DROP FUNCTION IF EXISTS match_chunks_por_area(vector, text, double precision, integer, text, text);
DROP FUNCTION IF EXISTS match_videos(vector, double precision, integer, text, text);
DROP FUNCTION IF EXISTS match_videos_por_area(vector, text, double precision, integer, text, text);

-- =========================================================
-- match_chunks (prod pré-20261014200000)
-- =========================================================

CREATE OR REPLACE FUNCTION match_chunks(
    query_embedding  VECTOR,
    match_threshold  DOUBLE PRECISION DEFAULT 0.35,
    match_count      INTEGER          DEFAULT 6
)
RETURNS TABLE (
    video_id        TEXT,
    titulo          TEXT,
    chunk_index     INTEGER,
    conteudo        TEXT,
    area            TEXT,
    subcategoria    TEXT,
    similarity      DOUBLE PRECISION
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
        1 - (vc.embedding <=> query_embedding::halfvec(1536)) AS similarity
    FROM video_chunk vc
    LEFT JOIN LATERAL (
        SELECT c.area, c.subcategoria
        FROM categoria_teologica c
        WHERE c.video_id = vc.video_id
        ORDER BY c.peso DESC
        LIMIT 1
    ) cat ON true
    WHERE 1 - (vc.embedding <=> query_embedding::halfvec(1536)) > match_threshold
    ORDER BY vc.embedding <=> query_embedding::halfvec(1536)
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks(vector, double precision, integer) TO anon, authenticated;

-- =========================================================
-- match_chunks_por_area (prod pré-20261014200000)
-- =========================================================

CREATE OR REPLACE FUNCTION match_chunks_por_area(
    query_embedding  VECTOR,
    area_filtro      TEXT,
    match_threshold  DOUBLE PRECISION DEFAULT 0.30,
    match_count      INTEGER          DEFAULT 6
)
RETURNS TABLE (
    video_id        TEXT,
    titulo          TEXT,
    chunk_index     INTEGER,
    conteudo        TEXT,
    area            TEXT,
    subcategoria    TEXT,
    similarity      DOUBLE PRECISION
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
        1 - (vc.embedding <=> query_embedding::halfvec(1536)) AS similarity
    FROM video_chunk vc
    INNER JOIN LATERAL (
        SELECT c.area, c.subcategoria
        FROM categoria_teologica c
        WHERE c.video_id = vc.video_id
          AND c.area ILIKE area_filtro
        ORDER BY c.peso DESC
        LIMIT 1
    ) cat ON true
    WHERE 1 - (vc.embedding <=> query_embedding::halfvec(1536)) > match_threshold
    ORDER BY vc.embedding <=> query_embedding::halfvec(1536)
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks_por_area(vector, text, double precision, integer) TO anon, authenticated;

-- =========================================================
-- match_videos (prod pré-20261014200000 — QUEBRADA: lê video_embedding, que não existe)
-- =========================================================

CREATE OR REPLACE FUNCTION match_videos(
    query_embedding  VECTOR,
    match_threshold  DOUBLE PRECISION DEFAULT 0.75,
    match_count      INTEGER          DEFAULT 5
)
RETURNS TABLE (
    video_id        TEXT,
    titulo          TEXT,
    conteudo        TEXT,
    area            TEXT,
    subcategoria    TEXT,
    similarity      DOUBLE PRECISION
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        ve.video_id,
        ve.titulo,
        ve.conteudo,
        cat.area,
        cat.subcategoria,
        1 - (ve.embedding <=> query_embedding) AS similarity
    FROM video_embedding ve
    LEFT JOIN categoria_teologica cat ON cat.video_id = ve.video_id
    WHERE 1 - (ve.embedding <=> query_embedding) > match_threshold
    ORDER BY ve.embedding <=> query_embedding
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_videos(vector, double precision, integer) TO anon, authenticated;

-- =========================================================
-- match_videos_por_area (prod pré-20261014200000 — QUEBRADA: lê video_embedding, que não existe)
-- =========================================================

CREATE OR REPLACE FUNCTION match_videos_por_area(
    query_embedding  VECTOR,
    area_filtro      TEXT,
    match_threshold  DOUBLE PRECISION DEFAULT 0.70,
    match_count      INTEGER          DEFAULT 5
)
RETURNS TABLE (
    video_id        TEXT,
    titulo          TEXT,
    conteudo        TEXT,
    area            TEXT,
    subcategoria    TEXT,
    similarity      DOUBLE PRECISION
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        ve.video_id,
        ve.titulo,
        ve.conteudo,
        cat.area,
        cat.subcategoria,
        1 - (ve.embedding <=> query_embedding) AS similarity
    FROM video_embedding ve
    INNER JOIN categoria_teologica cat ON cat.video_id = ve.video_id
    WHERE cat.area ILIKE area_filtro
      AND 1 - (ve.embedding <=> query_embedding) > match_threshold
    ORDER BY ve.embedding <=> query_embedding
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_videos_por_area(vector, text, double precision, integer) TO anon, authenticated;

COMMIT;
