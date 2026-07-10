-- =========================================================
-- Feature #101 — fallback de embedding entre provedores
--
-- Embeddings de provedores diferentes (OpenAI, Voyage, ...) não são comparáveis por
-- similaridade de cosseno entre si, mesmo com a mesma dimensão. Pra permitir que a cadeia de
-- fallback de embedding (.NET) acrescente um segundo provider no futuro sem corromper a busca
-- semântica, cada chunk passa a carregar a origem (provider) que gerou seu embedding, e as RPCs
-- híbridas passam a filtrar por origem — comparando só vetores do MESMO espaço vetorial.
--
-- Todos os chunks existentes hoje vieram do OpenAIEmbeddingService (OpenAI) — default 'OpenAI'
-- preenche o histórico sem precisar reindexar nada.
-- =========================================================

ALTER TABLE video_chunk
    ADD COLUMN IF NOT EXISTS origem TEXT NOT NULL DEFAULT 'OpenAI';

-- =========================================================
-- RPC: busca híbrida (vetor + full-text via RRF) — agora filtrando por origem
--
-- DROP explícito antes do CREATE: origem_filtro entra NO MEIO da lista de parâmetros (antes de
-- match_count), o que muda a assinatura por completo — sem o DROP, o Postgres cria uma
-- sobrecarga nova ao lado da antiga (ambígua pra chamada nomeada via PostgREST) em vez de
-- substituí-la. Existem 2 sobrecargas herdadas de migrações anteriores (sem e com
-- canal_id_filtro) — as duas precisam sair, senão o GRANT sem tipos abaixo fica ambíguo entre a
-- que sobrar e a nova.
-- =========================================================

DROP FUNCTION IF EXISTS match_chunks_hibrido(vector, text, int, int);
DROP FUNCTION IF EXISTS match_chunks_hibrido(vector, text, int, int, text);

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

GRANT EXECUTE ON FUNCTION match_chunks_hibrido TO anon, authenticated;

-- =========================================================
-- RPC: busca híbrida filtrada por área — agora filtrando por origem também (mesmo motivo do
-- DROP explícito acima, incluindo as 2 sobrecargas herdadas).
-- =========================================================

DROP FUNCTION IF EXISTS match_chunks_hibrido_por_area(vector, text, text, int, int);
DROP FUNCTION IF EXISTS match_chunks_hibrido_por_area(vector, text, text, int, int, text);

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
        WHERE vc.origem = origem_filtro
          AND EXISTS (SELECT 1 FROM categoria_teologica cc WHERE cc.video_id = vc.video_id AND cc.area ILIKE area_filtro)
        ORDER BY vc.embedding <=> query_embedding
        LIMIT candidatos
    ),
    textual AS (
        SELECT vc.id,
               ROW_NUMBER() OVER (ORDER BY ts_rank(vc.conteudo_tsv, websearch_to_tsquery('portuguese', query_texto)) DESC) AS rank_txt
        FROM video_chunk vc
        WHERE vc.origem = origem_filtro
          AND EXISTS (SELECT 1 FROM categoria_teologica cc WHERE cc.video_id = vc.video_id AND cc.area ILIKE area_filtro)
          AND vc.conteudo_tsv @@ websearch_to_tsquery('portuguese', query_texto)
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

GRANT EXECUTE ON FUNCTION match_chunks_hibrido_por_area TO anon, authenticated;
