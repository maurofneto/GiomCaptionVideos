-- =========================================================
-- Função PostgreSQL para busca semântica (RAG)
-- Chamada pelo C# via: POST /rest/v1/rpc/match_videos
-- =========================================================

CREATE OR REPLACE FUNCTION match_videos(
    query_embedding VECTOR(1536),
    match_threshold FLOAT    DEFAULT 0.75,
    match_count     INT      DEFAULT 5
)
RETURNS TABLE (
    video_id    TEXT,
    titulo      TEXT,
    conteudo    TEXT,
    area        TEXT,
    subcategoria TEXT,
    similarity  FLOAT
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

-- Permite chamada anônima (para o bot Telegram/WhatsApp)
GRANT EXECUTE ON FUNCTION match_videos TO anon, authenticated;

-- =========================================================
-- Função auxiliar: busca por área teológica + semântica
-- Útil para o agent filtrar por disciplina específica
-- =========================================================

CREATE OR REPLACE FUNCTION match_videos_por_area(
    query_embedding  VECTOR(1536),
    area_filtro      TEXT,
    match_threshold  FLOAT   DEFAULT 0.70,
    match_count      INT     DEFAULT 5
)
RETURNS TABLE (
    video_id    TEXT,
    titulo      TEXT,
    conteudo    TEXT,
    area        TEXT,
    subcategoria TEXT,
    similarity  FLOAT
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

GRANT EXECUTE ON FUNCTION match_videos_por_area TO anon, authenticated;
