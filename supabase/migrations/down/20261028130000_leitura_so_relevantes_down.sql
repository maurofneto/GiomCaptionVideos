-- DOWN de 20261028130000: volta a view, as funções match_* e a policy ao que eram antes do B2.
-- (Não é preciso reverter o código: o B2 só muda SQL.)

CREATE OR REPLACE VIEW v_saude_pipeline AS
WITH relevantes AS (
    SELECT id, channel_id, uploaded_at
    FROM video
), resumo_d AS (
    SELECT DISTINCT video_id FROM resumo
), categoria_d AS (
    SELECT DISTINCT video_id FROM categoria_teologica
), chunk_d AS (
    SELECT video_id, max(indexado_em) AS indexado_em
    FROM video_chunk
    GROUP BY video_id
), completos AS (
    SELECT rel.id, rel.channel_id, rel.uploaded_at, ch.indexado_em
    FROM relevantes rel
    JOIN resumo_d r ON r.video_id = rel.id
    JOIN categoria_d c ON c.video_id = rel.id
    JOIN chunk_d ch ON ch.video_id = rel.id
)
SELECT
    rel.channel_id,
    COALESCE(ci.nome, rel.channel_id) AS canal,
    count(*) AS total_videos,
    count(comp.id) AS videos_completos,
    round(100.0 * count(comp.id) / NULLIF(count(*), 0), 1) AS pct_completo,
    round(extract(epoch FROM avg(comp.indexado_em - comp.uploaded_at) FILTER (WHERE comp.uploaded_at >= DATE '2026-07-01')) / 3600.0, 1) AS atraso_medio_horas
FROM relevantes rel
LEFT JOIN completos comp ON comp.id = rel.id
LEFT JOIN canal_ingestao ci ON ci.channel_id = rel.channel_id
GROUP BY rel.channel_id, ci.nome
ORDER BY canal;

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


DROP POLICY IF EXISTS "leitura_publica_video" ON video;
CREATE POLICY "leitura_publica_video" ON video FOR SELECT USING (true);
