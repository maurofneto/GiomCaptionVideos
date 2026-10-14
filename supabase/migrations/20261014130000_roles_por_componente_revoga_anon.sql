-- =========================================================
-- US #149 — Acesso ao banco por componente (menor privilégio) + fim do acesso anônimo com escrita.
--
-- Contexto: no Postgres de DEV (Railway) o PostgREST conectava como o superusuário `postgres` e
-- usava essa mesma role como papel anônimo — qualquer pessoa, sem credencial, lia e escrevia em
-- todas as tabelas (ignorando RLS). Os workers gravavam como anônimos. Esta migration define as
-- roles de aplicação e os grants mínimos; a parte de infra (PostgREST sem papel anônimo, conexão
-- como `authenticator`, JWT por role) está em docs/runbook-banco-roles-e-migrations.md.
--
-- Modelo (padrão da migration 20261008120000, reconexao_leitura):
--   * Roles NOLOGIN assumidas pelo PostgREST conforme o claim `role` do JWT:
--       - pipeline_worker   : Ingestão, Transcrição, Resumo, Categorização, Embedding, Orquestração
--       - agente_bot        : AgenteInstituto (bot RAG)
--       - reconexao_leitura : página de reconexão (já existe; não é alterada aqui)
--   * `authenticator` (LOGIN, sem senha aqui) é a role de conexão do PostgREST — nunca superusuário.
--     A senha é definida fora da migration (bootstrap), nunca em arquivo versionado.
--   * Grants derivados do que o código realmente faz (Infrastructure.Supabase/*Repository.cs):
--     SELECT/INSERT/UPDATE/DELETE só nas tabelas e verbos usados. Upsert (on_conflict) = INSERT+UPDATE.
--   * RLS (ligado em quase todas as tabelas): uma policy permissiva por role/tabela. Sem policy, a
--     role não enxerga nada mesmo que um grant vaze — defesa em profundidade.
--
-- Idempotente e segura em Supabase e Postgres puro: tudo guardado por existência (role, tabela,
-- função). NÃO revoga LEITURA do `anon` (as policies leitura_publica_* existem de propósito);
-- revoga só ESCRITA do anon/authenticated, que nunca foi intenção de ninguém.
-- =========================================================

-- ---------- 1) Roles ----------
DO $$
DECLARE
    papel text;
BEGIN
    FOREACH papel IN ARRAY ARRAY['pipeline_worker', 'agente_bot'] LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = papel) THEN
            EXECUTE format('CREATE ROLE %I NOLOGIN NOINHERIT', papel);
        END IF;
    END LOOP;

    -- Role de conexão do PostgREST. Sem senha aqui (login por senha só passa a funcionar quando o
    -- bootstrap definir uma). No Supabase já existe e esta checagem não faz nada.
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticator') THEN
        CREATE ROLE authenticator LOGIN NOINHERIT NOCREATEDB NOCREATEROLE NOREPLICATION;
    END IF;

    -- authenticator precisa poder assumir a role do JWT (SET ROLE).
    FOREACH papel IN ARRAY ARRAY['pipeline_worker', 'agente_bot', 'reconexao_leitura'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = papel)
           AND NOT pg_has_role('authenticator', papel, 'MEMBER') THEN
            EXECUTE format('GRANT %I TO authenticator', papel);
        END IF;
    END LOOP;
END
$$;

GRANT USAGE ON SCHEMA public TO pipeline_worker, agente_bot;

-- ---------- 2) Escrita anônima fora ----------
DO $$
DECLARE
    papel text;
BEGIN
    FOREACH papel IN ARRAY ARRAY['anon', 'authenticated'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = papel) THEN
            EXECUTE format('REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON ALL TABLES IN SCHEMA public FROM %I', papel);
            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON TABLES FROM %I', papel);
        END IF;
    END LOOP;
END
$$;

-- Tabelas nunca devem ficar com grant pra PUBLIC (todas as roles).
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;

-- ---------- 3) Grants + policies por componente ----------
DO $$
DECLARE
    r record;
    policy_nome text;
BEGIN
    FOR r IN
        SELECT * FROM (VALUES
            -- pipeline_worker: todos os workers de etapa compartilham AddPersistence (mesmas tabelas)
            ('pipeline_worker', 'canal_ingestao',          'SELECT'),
            ('pipeline_worker', 'etapa_provedor_config',   'SELECT'),
            ('pipeline_worker', 'provedor_preco',          'SELECT'),
            ('pipeline_worker', 'video',                   'SELECT, INSERT, UPDATE'),
            ('pipeline_worker', 'transcricao',             'SELECT, INSERT, UPDATE'),
            ('pipeline_worker', 'resumo',                  'SELECT, INSERT, UPDATE'),
            ('pipeline_worker', 'categoria_teologica',     'SELECT, INSERT, UPDATE, DELETE'),
            ('pipeline_worker', 'video_chunk',             'SELECT, INSERT, UPDATE, DELETE'),
            ('pipeline_worker', 'processamento_progresso', 'SELECT, INSERT, UPDATE'),
            ('pipeline_worker', 'video_etapa_terminal',    'SELECT, INSERT, UPDATE'),
            ('pipeline_worker', 'provedor_rate_limit',     'SELECT, INSERT, UPDATE'),  -- farol_consumir é SECURITY INVOKER
            ('pipeline_worker', 'interacao_log',           'INSERT'),                  -- telemetria, sem return=representation
            -- agente_bot: lê o acervo, grava conversa/sessão e telemetria
            ('agente_bot', 'video',                 'SELECT'),
            ('agente_bot', 'transcricao',           'SELECT'),
            ('agente_bot', 'resumo',                'SELECT'),
            ('agente_bot', 'categoria_teologica',   'SELECT'),
            ('agente_bot', 'video_chunk',           'SELECT'),
            ('agente_bot', 'canal_ingestao',        'SELECT'),
            ('agente_bot', 'etapa_provedor_config', 'SELECT'),
            ('agente_bot', 'provedor_preco',        'SELECT'),
            ('agente_bot', 'conversa_historico',    'SELECT, INSERT, UPDATE'),
            ('agente_bot', 'chat_sessao',           'SELECT, INSERT, UPDATE'),
            ('agente_bot', 'provedor_rate_limit',   'SELECT, INSERT, UPDATE'),
            ('agente_bot', 'interacao_log',         'INSERT')
        ) AS t(papel, tabela, privilegios)
    LOOP
        CONTINUE WHEN to_regclass('public.' || r.tabela) IS NULL;

        -- Zera o que esta role já tinha na tabela (re-execução converge) e concede só o previsto.
        EXECUTE format('REVOKE ALL ON TABLE public.%I FROM %I', r.tabela, r.papel);
        EXECUTE format('GRANT %s ON TABLE public.%I TO %I', r.privilegios, r.tabela, r.papel);

        -- Com RLS ligado, sem policy a role não enxerga nada. A policy é permissiva (USING true):
        -- quem limita as operações são os grants acima.
        IF (SELECT relrowsecurity FROM pg_class WHERE oid = to_regclass('public.' || r.tabela)) THEN
            policy_nome := r.papel || '_acesso';
            EXECUTE format('DROP POLICY IF EXISTS %I ON public.%I', policy_nome, r.tabela);
            EXECUTE format('CREATE POLICY %I ON public.%I FOR ALL TO %I USING (true) WITH CHECK (true)',
                           policy_nome, r.tabela, r.papel);
        END IF;
    END LOOP;
END
$$;

-- Sequences (serial) usadas nos INSERTs.
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO pipeline_worker, agente_bot;

-- ---------- 4) Funções (RPC) ----------
DO $$
BEGIN
    IF to_regprocedure('public.farol_consumir(text, jsonb)') IS NOT NULL THEN
        REVOKE ALL ON FUNCTION public.farol_consumir(text, jsonb) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.farol_consumir(text, jsonb) TO pipeline_worker, agente_bot;
    END IF;
END
$$;
