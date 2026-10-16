-- =========================================================
-- Qualquer DDL pesado: rodar com SET statement_timeout = 0 e SET maintenance_work_mem = '64MB' (no máximo; 256MB falhou por falta de memória compartilhada no prod)
--
-- Filtros de canal e origem nas RPCs não-híbridas (match_chunks, match_chunks_por_area,
-- match_videos, match_videos_por_area), no mesmo padrão de match_chunks_hibrido:
--   canal_id_filtro TEXT DEFAULT NULL  -> restringe a video.channel_id (NULL = todos os canais)
--   origem_filtro   TEXT DEFAULT NULL  -> restringe a video_chunk.origem (NULL = todas as origens)
-- Os dois entram no FIM da lista de parâmetros, então chamadas posicionais/nomeadas existentes
-- continuam válidas.
--
-- Pré-requisito: 20261014170000 (video_chunk.embedding halfvec(1536)) já aplicada — por isso a
-- pergunta é convertida com query_embedding::halfvec(1536) na similaridade, no WHERE e no
-- ORDER BY (mesmo tipo da coluna, permite uso do índice HNSW halfvec).
--
-- DROP explícito da assinatura antiga antes do CREATE: acrescentar parâmetros muda a assinatura,
-- e sem o DROP o Postgres cria uma sobrecarga nova ao lado da antiga (ambígua pra chamada
-- nomeada via PostgREST) em vez de substituí-la.
--
-- Só funções (sem build de índice) — basta o statement_timeout.
-- =========================================================

BEGIN;

SET LOCAL statement_timeout = 0;

-- =========================================================
-- RPC: busca semântica nos chunks
-- =========================================================

DROP FUNCTION IF EXISTS match_chunks(vector, double precision, integer);

CREATE OR REPLACE FUNCTION match_chunks(
    query_embedding  VECTOR,
    match_threshold  DOUBLE PRECISION DEFAULT 0.35,
    match_count      INTEGER          DEFAULT 6,
    canal_id_filtro  TEXT             DEFAULT NULL,
    origem_filtro    TEXT             DEFAULT NULL
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
    JOIN video v ON v.id = vc.video_id
    LEFT JOIN LATERAL (
        SELECT c.area, c.subcategoria
        FROM categoria_teologica c
        WHERE c.video_id = vc.video_id
        ORDER BY c.peso DESC
        LIMIT 1
    ) cat ON true
    WHERE 1 - (vc.embedding <=> query_embedding::halfvec(1536)) > match_threshold
      AND (canal_id_filtro IS NULL OR v.channel_id = canal_id_filtro)
      AND (origem_filtro IS NULL OR vc.origem = origem_filtro)
    ORDER BY vc.embedding <=> query_embedding::halfvec(1536)
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks(vector, double precision, integer, text, text) TO anon, authenticated;

-- =========================================================
-- RPC: busca semântica nos chunks filtrada por área teológica
-- =========================================================

DROP FUNCTION IF EXISTS match_chunks_por_area(vector, text, double precision, integer);

CREATE OR REPLACE FUNCTION match_chunks_por_area(
    query_embedding  VECTOR,
    area_filtro      TEXT,
    match_threshold  DOUBLE PRECISION DEFAULT 0.30,
    match_count      INTEGER          DEFAULT 6,
    canal_id_filtro  TEXT             DEFAULT NULL,
    origem_filtro    TEXT             DEFAULT NULL
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
    JOIN video v ON v.id = vc.video_id
    INNER JOIN LATERAL (
        SELECT c.area, c.subcategoria
        FROM categoria_teologica c
        WHERE c.video_id = vc.video_id
          AND c.area ILIKE area_filtro
        ORDER BY c.peso DESC
        LIMIT 1
    ) cat ON true
    WHERE 1 - (vc.embedding <=> query_embedding::halfvec(1536)) > match_threshold
      AND (canal_id_filtro IS NULL OR v.channel_id = canal_id_filtro)
      AND (origem_filtro IS NULL OR vc.origem = origem_filtro)
    ORDER BY vc.embedding <=> query_embedding::halfvec(1536)
    LIMIT match_count;
END;
$$;

GRANT EXECUTE ON FUNCTION match_chunks_por_area(vector, text, double precision, integer, text, text) TO anon, authenticated;

-- =========================================================
-- RPC: busca semântica por vídeo
--
-- Antes lia video_embedding (1 embedding por vídeo), tabela que não existe mais desde
-- 20260706160000_chunking_rag.sql (renomeada pra video_embedding_deprecated) — a função estava
-- quebrada em prod. Agora lê video_chunk: cada vídeo é representado pelo seu MELHOR chunk
-- (o mais próximo da pergunta, via DISTINCT ON (vc.video_id) ordenado por distância numa
-- subquery), e os vídeos são ordenados por essa distância e cortados em match_count.
-- conteudo/similarity são os desse melhor chunk; área/subcategoria = categoria_teologica de
-- maior peso do vídeo.
--
-- O DISTINCT ON varre todos os chunks acima do threshold (não usa o HNSW pra cortar
-- candidatos) — aceitável no volume atual (milhares de chunks) e garante que nenhum vídeo
-- relevante fique de fora por corte prematuro.
-- =========================================================

DROP FUNCTION IF EXISTS match_videos(vector, double precision, integer);

CREATE OR REPLACE FUNCTION match_videos(
    query_embedding  VECTOR,
    match_threshold  DOUBLE PRECISION DEFAULT 0.75,
    match_count      INTEGER          DEFAULT 5,
    canal_id_filtro  TEXT             DEFAULT NULL,
    origem_filtro    TEXT             DEFAULT NULL
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
        melhor.video_id,
        melhor.titulo,
        melhor.conteudo,
        cat.area,
        cat.subcategoria,
        1 - melhor.distancia AS similarity
    FROM (
        SELECT DISTINCT ON (vc.video_id)
            vc.video_id,
            vc.titulo,
            vc.conteudo,
            vc.embedding <=> query_embedding::halfvec(1536) AS distancia
        FROM video_chunk vc
        JOIN video v ON v.id = vc.video_id
        WHERE 1 - (vc.embedding <=> query_embedding::halfvec(1536)) > match_threshold
          AND (canal_id_filtro IS NULL OR v.channel_id = canal_id_filtro)
          AND (origem_filtro IS NULL OR vc.origem = origem_filtro)
        ORDER BY vc.video_id, vc.embedding <=> query_embedding::halfvec(1536)
    ) melhor
    LEFT JOIN LATERAL (
        SELECT c.area, c.subcategoria
        FROM categoria_teologica c
        WHERE c.video_id = melhor.video_id
        ORDER BY c.peso DESC
        LIMIT 1
    ) cat ON true
    ORDER BY melhor.distancia, melhor.video_id
    LIMIT match_count;
END;
$$;

COMMENT ON FUNCTION match_videos(vector, double precision, integer, text, text) IS
    'Um resultado por vídeo = seu chunk mais próximo da pergunta (video_chunk; video_embedding foi descontinuada). Filtros opcionais por canal (video.channel_id) e origem do embedding (video_chunk.origem).';

GRANT EXECUTE ON FUNCTION match_videos(vector, double precision, integer, text, text) TO anon, authenticated;

-- =========================================================
-- RPC: busca semântica por vídeo filtrada por área teológica
--
-- Mesma estratégia de match_videos (melhor chunk por vídeo em video_chunk); só entram vídeos
-- com alguma categoria_teologica cuja área casa com area_filtro (ILIKE), e a área/subcategoria
-- devolvidas são as da categoria de maior peso entre as que casam.
-- =========================================================

DROP FUNCTION IF EXISTS match_videos_por_area(vector, text, double precision, integer);

CREATE OR REPLACE FUNCTION match_videos_por_area(
    query_embedding  VECTOR,
    area_filtro      TEXT,
    match_threshold  DOUBLE PRECISION DEFAULT 0.70,
    match_count      INTEGER          DEFAULT 5,
    canal_id_filtro  TEXT             DEFAULT NULL,
    origem_filtro    TEXT             DEFAULT NULL
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
        melhor.video_id,
        melhor.titulo,
        melhor.conteudo,
        cat.area,
        cat.subcategoria,
        1 - melhor.distancia AS similarity
    FROM (
        SELECT DISTINCT ON (vc.video_id)
            vc.video_id,
            vc.titulo,
            vc.conteudo,
            vc.embedding <=> query_embedding::halfvec(1536) AS distancia
        FROM video_chunk vc
        JOIN video v ON v.id = vc.video_id
        WHERE 1 - (vc.embedding <=> query_embedding::halfvec(1536)) > match_threshold
          AND (canal_id_filtro IS NULL OR v.channel_id = canal_id_filtro)
          AND (origem_filtro IS NULL OR vc.origem = origem_filtro)
        ORDER BY vc.video_id, vc.embedding <=> query_embedding::halfvec(1536)
    ) melhor
    INNER JOIN LATERAL (
        SELECT c.area, c.subcategoria
        FROM categoria_teologica c
        WHERE c.video_id = melhor.video_id
          AND c.area ILIKE area_filtro
        ORDER BY c.peso DESC
        LIMIT 1
    ) cat ON true
    ORDER BY melhor.distancia, melhor.video_id
    LIMIT match_count;
END;
$$;

COMMENT ON FUNCTION match_videos_por_area(vector, text, double precision, integer, text, text) IS
    'Um resultado por vídeo = seu chunk mais próximo da pergunta (video_chunk; video_embedding foi descontinuada), só vídeos com categoria_teologica.area ILIKE area_filtro. Filtros opcionais por canal (video.channel_id) e origem do embedding (video_chunk.origem).';

GRANT EXECUTE ON FUNCTION match_videos_por_area(vector, text, double precision, integer, text, text) TO anon, authenticated;

COMMIT;
