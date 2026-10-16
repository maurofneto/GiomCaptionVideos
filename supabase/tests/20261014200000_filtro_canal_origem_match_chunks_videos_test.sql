-- =========================================================
-- Teste da migration 20261014200000_filtro_canal_origem_match_chunks_videos (+ down).
--
-- Rodar SÓ num banco descartável com pgvector >= 0.7 (halfvec), ex.:
--   createdb teste_rag
--   psql -d teste_rag -f supabase/tests/20261014200000_filtro_canal_origem_match_chunks_videos_test.sql
--
-- Cria tudo no schema teste_match (primeiro do search_path) e o remove no fim. Aborta se já
-- existir match_chunks/match_videos* em outro schema — os DROP FUNCTION IF EXISTS das
-- migrations resolvem pelo search_path e poderiam atingir as funções de um banco real.
--
-- Roteiro: DOWN (estado atual de prod) -> UP (asserts dos filtros) -> DOWN (asserts do
-- rollback) -> UP de novo (reaplicável após rollback).
--
-- Massa: 2 canais (UC_INSTITUTO, UC_MINISTERIO), 3 vídeos, chunks de 3 origens; pergunta =
-- [1,0,...]; chunk [1,b,...] tem similaridade 1/sqrt(1+b²).
--   vi1 (Instituto)  OpenAI  idx0 b=0.10 (0.995) | OpenAI idx1 b=0.50 (0.894) | VoyageAI idx0 b=0.20 (0.981)
--   vm1 (Ministério) OpenAI  idx0 b=0.15 (0.989) | OpenAI idx1 [0,1] (0.0)    | VoyageAI idx0 b=0.25 (0.970)
--   vi2 (Instituto)  Cohere  idx0 b=0.30 (0.958)
--   categorias: vi1 Soteriologia 0.9 + Escatologia 0.3 | vm1 Escatologia 0.8 | vi2 nenhuma
-- =========================================================

\set ON_ERROR_STOP on

CREATE EXTENSION IF NOT EXISTS vector;

DO $$
DECLARE
    versao int[] := string_to_array(split_part((SELECT extversion FROM pg_extension WHERE extname = 'vector'), '-', 1), '.')::int[];
BEGIN
    IF versao < ARRAY[0, 7, 0] THEN
        RAISE EXCEPTION 'pgvector % não tem halfvec — precisa >= 0.7.0', array_to_string(versao, '.');
    END IF;

    IF EXISTS (
        SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE p.proname IN ('match_chunks', 'match_chunks_por_area', 'match_videos', 'match_videos_por_area')
          AND n.nspname <> 'teste_match'
    ) THEN
        RAISE EXCEPTION 'Já existem funções match_* fora de teste_match — rode este teste num banco descartável';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
        CREATE ROLE anon NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
        CREATE ROLE authenticated NOLOGIN;
    END IF;
END
$$;

DROP SCHEMA IF EXISTS teste_match CASCADE;
CREATE SCHEMA teste_match;
SET search_path = teste_match, public, extensions;

-- ---------------------------------------------------------
-- Schema mínimo (colunas usadas pelas RPCs)
-- ---------------------------------------------------------

CREATE TABLE video (
    id          TEXT PRIMARY KEY,
    channel_id  TEXT,
    title       TEXT
);

CREATE TABLE categoria_teologica (
    id            SERIAL PRIMARY KEY,
    video_id      TEXT NOT NULL REFERENCES video(id) ON DELETE CASCADE,
    area          TEXT,
    subcategoria  TEXT,
    peso          NUMERIC
);

CREATE TABLE video_chunk (
    id           SERIAL PRIMARY KEY,
    video_id     TEXT NOT NULL REFERENCES video(id) ON DELETE CASCADE,
    titulo       TEXT,
    chunk_index  INT  NOT NULL,
    conteudo     TEXT NOT NULL,
    embedding    HALFVEC(1536),
    origem       TEXT DEFAULT 'OpenAI',
    UNIQUE (video_id, chunk_index, origem)
);

CREATE FUNCTION teste_match.vetor(a DOUBLE PRECISION, b DOUBLE PRECISION) RETURNS vector
LANGUAGE sql IMMUTABLE AS $$
    SELECT ('[' || a || ',' || b || repeat(',0', 1534) || ']')::vector(1536)
$$;

CREATE FUNCTION teste_match.checar(condicao BOOLEAN, descricao TEXT) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF condicao IS NOT TRUE THEN
        RAISE EXCEPTION 'FALHOU: %', descricao;
    END IF;
    RAISE NOTICE 'ok: %', descricao;
END;
$$;

INSERT INTO video (id, channel_id, title) VALUES
    ('vi1', 'UC_INSTITUTO',  'Aula Instituto 1'),
    ('vi2', 'UC_INSTITUTO',  'Aula Instituto 2'),
    ('vm1', 'UC_MINISTERIO', 'Culto Ministério 1');

INSERT INTO categoria_teologica (video_id, area, subcategoria, peso) VALUES
    ('vi1', 'Soteriologia', 'Justificação',  0.9),
    ('vi1', 'Escatologia',  'Arrebatamento', 0.3),
    ('vm1', 'Escatologia',  'Milênio',       0.8);

INSERT INTO video_chunk (video_id, titulo, chunk_index, conteudo, embedding, origem) VALUES
    ('vi1', 'Aula Instituto 1',   0, 'vi1-openai-0', teste_match.vetor(1, 0.10)::halfvec(1536), 'OpenAI'),
    ('vi1', 'Aula Instituto 1',   1, 'vi1-openai-1', teste_match.vetor(1, 0.50)::halfvec(1536), 'OpenAI'),
    ('vi1', 'Aula Instituto 1',   0, 'vi1-voyage-0', teste_match.vetor(1, 0.20)::halfvec(1536), 'VoyageAI'),
    ('vm1', 'Culto Ministério 1', 0, 'vm1-openai-0', teste_match.vetor(1, 0.15)::halfvec(1536), 'OpenAI'),
    ('vm1', 'Culto Ministério 1', 1, 'vm1-openai-1', teste_match.vetor(0, 1)::halfvec(1536),    'OpenAI'),
    ('vm1', 'Culto Ministério 1', 0, 'vm1-voyage-0', teste_match.vetor(1, 0.25)::halfvec(1536), 'VoyageAI'),
    ('vi2', 'Aula Instituto 2',   0, 'vi2-cohere-0', teste_match.vetor(1, 0.30)::halfvec(1536), 'Cohere');

-- ---------------------------------------------------------
-- Estado de prod antes da migration (assinaturas antigas) e UP
-- ---------------------------------------------------------

\ir ../migrations/down/20261014200000_filtro_canal_origem_match_chunks_videos_down.sql
\ir ../migrations/20261014200000_filtro_canal_origem_match_chunks_videos.sql

-- ---------------------------------------------------------
-- Assinaturas: só a nova de cada função (sem sobrecarga ambígua)
-- ---------------------------------------------------------

DO $$
BEGIN
    PERFORM teste_match.checar(
        (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
         WHERE n.nspname = 'teste_match' AND p.proname = f) = 1,
        f || ': exatamente 1 sobrecarga após UP')
    FROM unnest(ARRAY['match_chunks', 'match_chunks_por_area', 'match_videos', 'match_videos_por_area']) f;

    PERFORM teste_match.checar(to_regprocedure('match_chunks(vector,double precision,integer,text,text)') IS NOT NULL, 'match_chunks: assinatura nova existe');
    PERFORM teste_match.checar(to_regprocedure('match_chunks_por_area(vector,text,double precision,integer,text,text)') IS NOT NULL, 'match_chunks_por_area: assinatura nova existe');
    PERFORM teste_match.checar(to_regprocedure('match_videos(vector,double precision,integer,text,text)') IS NOT NULL, 'match_videos: assinatura nova existe');
    PERFORM teste_match.checar(to_regprocedure('match_videos_por_area(vector,text,double precision,integer,text,text)') IS NOT NULL, 'match_videos_por_area: assinatura nova existe');
END
$$;

-- ---------------------------------------------------------
-- match_chunks
-- ---------------------------------------------------------

DO $$
DECLARE
    q vector := teste_match.vetor(1, 0);
BEGIN
    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks(q, 0.5, 100)) = 6,
        'match_chunks NULL/NULL: chunks dos 2 canais e 3 origens (6, sem o abaixo do threshold)');
    PERFORM teste_match.checar((SELECT conteudo FROM match_chunks(q, 0.5, 100) LIMIT 1) = 'vi1-openai-0',
        'match_chunks: ordenado por similaridade');
    PERFORM teste_match.checar(NOT EXISTS (SELECT 1 FROM match_chunks(q, 0.5, 100) WHERE conteudo = 'vm1-openai-1'),
        'match_chunks: respeita match_threshold');

    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks(q, 0.5, 100, 'UC_INSTITUTO')) = 4,
        'match_chunks canal Instituto: 4 chunks');
    PERFORM teste_match.checar(NOT EXISTS (SELECT 1 FROM match_chunks(q, 0.5, 100, 'UC_INSTITUTO') WHERE video_id = 'vm1'),
        'match_chunks canal Instituto: nenhum chunk do Ministério');
    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks(q, 0.5, 100, 'UC_MINISTERIO')) = 2,
        'match_chunks canal Ministério: 2 chunks');

    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks(q, 0.5, 100, NULL, 'VoyageAI')) = 2,
        'match_chunks origem VoyageAI: 2 chunks (os 2 canais)');
    PERFORM teste_match.checar((SELECT count(DISTINCT video_id) FROM match_chunks(q, 0.5, 100, NULL, 'VoyageAI')) = 2,
        'match_chunks origem VoyageAI: sem filtro de canal traz vi1 e vm1');
    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks(q, 0.5, 100, NULL, 'Cohere')) = 1,
        'match_chunks origem Cohere: 1 chunk');

    PERFORM teste_match.checar((SELECT array_agg(conteudo) FROM match_chunks(q, 0.5, 100, 'UC_INSTITUTO', 'VoyageAI')) = ARRAY['vi1-voyage-0'],
        'match_chunks canal Instituto + origem VoyageAI: só vi1-voyage-0');
    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks(q, 0.5, 100, 'UC_MINISTERIO', 'Cohere')) = 0,
        'match_chunks canal Ministério + origem Cohere: vazio');

    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks(query_embedding => q, canal_id_filtro => 'UC_MINISTERIO')) = 2,
        'match_chunks chamada nomeada (estilo PostgREST) só com canal_id_filtro, demais defaults');
    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks(query_embedding => q, origem_filtro => 'OpenAI', match_threshold => 0.5)) = 3,
        'match_chunks chamada nomeada só com origem_filtro');

    PERFORM teste_match.checar((SELECT bool_and(area = 'Soteriologia') FROM match_chunks(q, 0.5, 100) WHERE video_id = 'vi1'),
        'match_chunks: categoria = a de maior peso do vídeo');
    PERFORM teste_match.checar((SELECT bool_and(area IS NULL) FROM match_chunks(q, 0.5, 100) WHERE video_id = 'vi2')
                               AND EXISTS (SELECT 1 FROM match_chunks(q, 0.5, 100) WHERE video_id = 'vi2'),
        'match_chunks: vídeo sem categoria continua no resultado (LEFT JOIN)');
END
$$;

-- ---------------------------------------------------------
-- match_chunks_por_area
-- ---------------------------------------------------------

DO $$
DECLARE
    q vector := teste_match.vetor(1, 0);
BEGIN
    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks_por_area(q, 'escatologia', 0.5, 100)) = 5,
        'match_chunks_por_area escatologia NULL/NULL: vi1 (3) + vm1 (2)');
    PERFORM teste_match.checar((SELECT bool_and(area = 'Escatologia' AND subcategoria = 'Arrebatamento')
                                FROM match_chunks_por_area(q, 'escatologia', 0.5, 100) WHERE video_id = 'vi1'),
        'match_chunks_por_area: categoria devolvida é a da área filtrada');
    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks_por_area(q, 'SOTERIOLOGIA', 0.5, 100)) = 3,
        'match_chunks_por_area: ILIKE case-insensitive');

    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks_por_area(q, 'escatologia', 0.5, 100, 'UC_INSTITUTO')) = 3,
        'match_chunks_por_area canal Instituto: só vi1');
    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks_por_area(q, 'soteriologia', 0.5, 100, 'UC_MINISTERIO')) = 0,
        'match_chunks_por_area canal Ministério + soteriologia: vazio');

    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks_por_area(q, 'escatologia', 0.5, 100, NULL, 'OpenAI')) = 3,
        'match_chunks_por_area origem OpenAI: vi1 (2) + vm1 (1)');
    PERFORM teste_match.checar((SELECT array_agg(conteudo) FROM match_chunks_por_area(q, 'escatologia', 0.5, 100, 'UC_MINISTERIO', 'VoyageAI')) = ARRAY['vm1-voyage-0'],
        'match_chunks_por_area canal Ministério + origem VoyageAI: só vm1-voyage-0');
END
$$;

-- ---------------------------------------------------------
-- match_videos (1 linha por vídeo = melhor chunk)
-- ---------------------------------------------------------

DO $$
DECLARE
    q vector := teste_match.vetor(1, 0);
BEGIN
    PERFORM teste_match.checar((SELECT array_agg(video_id) FROM match_videos(q, 0.5, 100)) = ARRAY['vi1', 'vm1', 'vi2'],
        'match_videos NULL/NULL: 1 linha por vídeo, ordenado pela melhor similaridade');
    PERFORM teste_match.checar((SELECT conteudo FROM match_videos(q, 0.5, 100) WHERE video_id = 'vi1') = 'vi1-openai-0',
        'match_videos: conteudo = melhor chunk do vídeo');
    PERFORM teste_match.checar((SELECT abs(similarity - 1 / sqrt(1 + 0.01)) < 0.001 FROM match_videos(q, 0.5, 100) WHERE video_id = 'vi1'),
        'match_videos: similarity = do melhor chunk');
    PERFORM teste_match.checar((SELECT area FROM match_videos(q, 0.5, 100) WHERE video_id = 'vi1') = 'Soteriologia',
        'match_videos: categoria = a de maior peso');
    PERFORM teste_match.checar((SELECT count(*) FROM match_videos(q)) = 3,
        'match_videos: defaults (threshold 0.75, count 5)');
    PERFORM teste_match.checar((SELECT array_agg(video_id) FROM match_videos(q, 0.5, 1)) = ARRAY['vi1'],
        'match_videos: match_count conta vídeos, não chunks');

    PERFORM teste_match.checar((SELECT array_agg(video_id) FROM match_videos(q, 0.5, 100, 'UC_INSTITUTO')) = ARRAY['vi1', 'vi2'],
        'match_videos canal Instituto: só vídeos do Instituto');
    PERFORM teste_match.checar((SELECT array_agg(conteudo) FROM match_videos(q, 0.5, 100, NULL, 'VoyageAI')) = ARRAY['vi1-voyage-0', 'vm1-voyage-0'],
        'match_videos origem VoyageAI: melhor chunk DA ORIGEM em cada vídeo, os 2 canais');
    PERFORM teste_match.checar((SELECT array_agg(conteudo) FROM match_videos(q, 0.5, 100, 'UC_MINISTERIO', 'VoyageAI')) = ARRAY['vm1-voyage-0'],
        'match_videos canal Ministério + origem VoyageAI');
    PERFORM teste_match.checar((SELECT count(*) FROM match_videos(q, 0.5, 100, 'UC_MINISTERIO', 'Cohere')) = 0,
        'match_videos canal Ministério + origem Cohere: vazio');
    PERFORM teste_match.checar((SELECT count(*) FROM match_videos(query_embedding => q, match_threshold => 0.5, origem_filtro => 'Cohere')) = 1,
        'match_videos chamada nomeada só com origem_filtro');
END
$$;

-- ---------------------------------------------------------
-- match_videos_por_area
-- ---------------------------------------------------------

DO $$
DECLARE
    q vector := teste_match.vetor(1, 0);
BEGIN
    PERFORM teste_match.checar((SELECT array_agg(video_id) FROM match_videos_por_area(q, 'escatologia', 0.5, 100)) = ARRAY['vi1', 'vm1'],
        'match_videos_por_area escatologia NULL/NULL: vi1 e vm1');
    PERFORM teste_match.checar((SELECT area FROM match_videos_por_area(q, 'escatologia', 0.5, 100) WHERE video_id = 'vi1') = 'Escatologia',
        'match_videos_por_area: categoria devolvida é a da área filtrada');
    PERFORM teste_match.checar((SELECT array_agg(video_id) FROM match_videos_por_area(q, 'escatologia', 0.5, 100, 'UC_MINISTERIO')) = ARRAY['vm1'],
        'match_videos_por_area canal Ministério: só vm1');
    PERFORM teste_match.checar((SELECT array_agg(conteudo) FROM match_videos_por_area(q, 'soteriologia', 0.5, 100, NULL, 'VoyageAI')) = ARRAY['vi1-voyage-0'],
        'match_videos_por_area origem VoyageAI: melhor chunk da origem');
    PERFORM teste_match.checar((SELECT count(*) FROM match_videos_por_area(q, 'escatologia', 0.5, 100, NULL, 'Cohere')) = 0,
        'match_videos_por_area origem Cohere: vazio (vi2 não tem categoria)');
END
$$;

-- ---------------------------------------------------------
-- DOWN: volta às assinaturas antigas (match_videos* quebradas, como em prod)
-- ---------------------------------------------------------

\ir ../migrations/down/20261014200000_filtro_canal_origem_match_chunks_videos_down.sql

DO $$
DECLARE
    q vector := teste_match.vetor(1, 0);
BEGIN
    PERFORM teste_match.checar(to_regprocedure('match_chunks(vector,double precision,integer,text,text)') IS NULL
                               AND to_regprocedure('match_chunks(vector,double precision,integer)') IS NOT NULL,
        'DOWN match_chunks: assinatura antiga restaurada');
    PERFORM teste_match.checar(to_regprocedure('match_chunks_por_area(vector,text,double precision,integer,text,text)') IS NULL
                               AND to_regprocedure('match_chunks_por_area(vector,text,double precision,integer)') IS NOT NULL,
        'DOWN match_chunks_por_area: assinatura antiga restaurada');
    PERFORM teste_match.checar(to_regprocedure('match_videos(vector,double precision,integer,text,text)') IS NULL
                               AND to_regprocedure('match_videos(vector,double precision,integer)') IS NOT NULL,
        'DOWN match_videos: assinatura antiga restaurada');
    PERFORM teste_match.checar(to_regprocedure('match_videos_por_area(vector,text,double precision,integer,text,text)') IS NULL
                               AND to_regprocedure('match_videos_por_area(vector,text,double precision,integer)') IS NOT NULL,
        'DOWN match_videos_por_area: assinatura antiga restaurada');

    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks(q, 0.5, 100)) = 6,
        'DOWN match_chunks: funciona sem filtros');

    BEGIN
        PERFORM 1 FROM match_videos(q, 0.5, 100);
        PERFORM teste_match.checar(false, 'DOWN match_videos: deveria falhar (video_embedding não existe)');
    EXCEPTION WHEN undefined_table THEN
        PERFORM teste_match.checar(true, 'DOWN match_videos: quebrada como em prod (video_embedding não existe)');
    END;
END
$$;

-- ---------------------------------------------------------
-- UP de novo: reaplicável depois do rollback
-- ---------------------------------------------------------

\ir ../migrations/20261014200000_filtro_canal_origem_match_chunks_videos.sql

DO $$
DECLARE
    q vector := teste_match.vetor(1, 0);
BEGIN
    PERFORM teste_match.checar((SELECT count(*) FROM match_chunks(q, 0.5, 100, 'UC_INSTITUTO', 'OpenAI')) = 2,
        'UP reaplicado: match_chunks com filtros');
    PERFORM teste_match.checar((SELECT count(*) FROM match_videos(q, 0.5, 100, 'UC_INSTITUTO')) = 2,
        'UP reaplicado: match_videos com filtros');
END
$$;

RESET search_path;
DROP SCHEMA teste_match CASCADE;

\echo 'OK: todos os asserts de 20261014200000_filtro_canal_origem_match_chunks_videos passaram'
