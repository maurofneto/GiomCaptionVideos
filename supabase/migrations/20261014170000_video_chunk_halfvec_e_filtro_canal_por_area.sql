-- =========================================================
-- video_chunk.embedding: vector(1536) -> halfvec(1536) + HNSW halfvec_cosine_ops;
-- corrige canal_id_filtro ignorado em match_chunks_hibrido_por_area.
--
-- Requer pgvector >= 0.7 (halfvec + cast vector <-> halfvec); dev está em 0.8.2.
--
-- 1. halfvec (float16) guarda o mesmo vetor com metade do espaço, na tabela e no índice HNSW.
--    A perda de precisão (~3 dígitos significativos) é irrelevante pra similaridade de cosseno
--    de embeddings normalizados (OpenAI/Cohere/Voyage-com-zeros ficam em [-1, 1], longe do
--    limite de ±65504 do float16). O índice é dropado ANTES do ALTER TYPE: o Postgres tentaria
--    reconstruí-lo com vector_cosine_ops sobre a coluna halfvec e falharia. O ALTER reescreve a
--    tabela sob ACCESS EXCLUSIVE lock — rápido no volume atual, mas bloqueia a busca do bot
--    durante a reescrita + rebuild do HNSW.
--
-- 2. As 4 RPCs mantêm a assinatura (query_embedding continua `vector`), então a Edge Function
--    buscar-similar e o .NET não mudam. Dentro delas, query_embedding::halfvec(1536) em TODA
--    comparação com vc.embedding (WHERE, ORDER BY, ROW_NUMBER OVER e similarity) — sem o cast
--    o operador <=> não casa com halfvec_cosine_ops e o HNSW não é usado.
--
-- 3. match_chunks_hibrido_por_area aceitava canal_id_filtro mas não filtrava por ele (busca
--    por área vazava chunks de outros canais). Agora usa o mesmo JOIN video + filtro de
--    match_chunks_hibrido nas duas CTEs.
--
-- Escritas não mudam: o PostgREST converte o array JSON do embedding pra texto '[...]', que o
-- input de halfvec aceita igual ao de vector.
--
-- Down (manual, NÃO aplicado pelo `supabase db push`, que ignora subdiretórios):
--   supabase/migrations/down/20261014170000_video_chunk_halfvec_e_filtro_canal_por_area_down.sql
-- =========================================================

DROP INDEX IF EXISTS idx_video_chunk_hnsw;

ALTER TABLE video_chunk
    ALTER COLUMN embedding TYPE halfvec(1536) USING embedding::halfvec(1536);

CREATE INDEX idx_video_chunk_hnsw
    ON video_chunk
    USING hnsw (embedding halfvec_cosine_ops)
    WITH (m = 16, ef_construction = 64);

-- =========================================================
-- RPC: busca semântica nos chunks
-- =========================================================

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
        1 - (vc.embedding <=> query_embedding::halfvec(1536)) AS similarity
    FROM video_chunk vc
    LEFT JOIN LATERAL (
        SELECT c.area, c.subcategoria FROM categoria_teologica c
        WHERE c.video_id = vc.video_id ORDER BY c.peso DESC LIMIT 1
    ) cat ON true
    WHERE 1 - (vc.embedding <=> query_embedding::halfvec(1536)) > match_threshold
    ORDER BY vc.embedding <=> query_embedding::halfvec(1536)
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks(vector, double precision, integer) TO anon, authenticated;

-- =========================================================
-- RPC: busca semântica filtrada por área teológica
-- =========================================================

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
        1 - (vc.embedding <=> query_embedding::halfvec(1536)) AS similarity
    FROM video_chunk vc
    INNER JOIN LATERAL (
        SELECT c.area, c.subcategoria, c.peso FROM categoria_teologica c
        WHERE c.video_id = vc.video_id AND c.area ILIKE area_filtro
        ORDER BY c.peso DESC LIMIT 1
    ) cat ON true
    WHERE 1 - (vc.embedding <=> query_embedding::halfvec(1536)) > match_threshold
    ORDER BY vc.embedding <=> query_embedding::halfvec(1536)
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks_por_area(vector, text, double precision, integer) TO anon, authenticated;

-- =========================================================
-- RPC: busca híbrida (vetor + full-text via RRF), filtrando por origem e canal
-- =========================================================

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
        SELECT vc.id, ROW_NUMBER() OVER (ORDER BY vc.embedding <=> query_embedding::halfvec(1536)) AS rank_sem
        FROM video_chunk vc
        JOIN video v ON v.id = vc.video_id
        WHERE vc.origem = origem_filtro
          AND (canal_id_filtro IS NULL OR v.channel_id = canal_id_filtro)
        ORDER BY vc.embedding <=> query_embedding::halfvec(1536)
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
           1 - (vc.embedding <=> query_embedding::halfvec(1536)) AS similarity
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

-- =========================================================
-- RPC: busca híbrida filtrada por área — agora respeitando canal_id_filtro também
-- =========================================================

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
        SELECT vc.id, ROW_NUMBER() OVER (ORDER BY vc.embedding <=> query_embedding::halfvec(1536)) AS rank_sem
        FROM video_chunk vc
        JOIN video v ON v.id = vc.video_id
        WHERE vc.origem = origem_filtro
          AND EXISTS (SELECT 1 FROM categoria_teologica cc WHERE cc.video_id = vc.video_id AND cc.area ILIKE area_filtro)
          AND (canal_id_filtro IS NULL OR v.channel_id = canal_id_filtro)
        ORDER BY vc.embedding <=> query_embedding::halfvec(1536)
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
           1 - (vc.embedding <=> query_embedding::halfvec(1536)) AS similarity
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
