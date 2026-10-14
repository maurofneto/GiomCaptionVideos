-- =========================================================
-- Bootstrap ÚNICO do Postgres do Railway (dev/prod) — US #149. Rodar como superusuário, UMA vez
-- por ambiente (é idempotente: pode repetir). Depois disso, o pipeline aplica as migrations como
-- `migrator` (sem superusuário) e o PostgREST conecta como `authenticator` (sem superusuário).
--
-- Senhas entram SÓ por variável do psql — nunca neste arquivo, nunca no git:
--   psql "$URL_ADMIN" -X -v ON_ERROR_STOP=1 \
--        -v authenticator_password="$AUTH_PW" -v migrator_password="$MIG_PW" \
--        -f scripts/db/bootstrap-railway.sql
-- Pré-requisito: a migration 20261014130000 já aplicada (cria authenticator e as roles por componente).
-- =========================================================

-- 1) Senha do login do PostgREST (a role é criada pela migration, sem senha).
ALTER ROLE authenticator PASSWORD :'authenticator_password';

-- 2) Role do pipeline de migrations: login, sem superusuário, sem criar roles/bancos.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'migrator') THEN
        CREATE ROLE migrator LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
    END IF;
END
$$;
ALTER ROLE migrator PASSWORD :'migrator_password';

-- 3) migrator vira dono dos objetos de public (DDL exige ser dono) e pode criar objetos novos.
GRANT CREATE, USAGE ON SCHEMA public TO migrator;
DO $$
DECLARE
    o record;
BEGIN
    FOR o IN
        SELECT format('ALTER %s public.%I OWNER TO migrator',
                      CASE c.relkind WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'MATERIALIZED VIEW' ELSE 'TABLE' END,
                      c.relname) AS cmd
          FROM pg_class c
         WHERE c.relnamespace = 'public'::regnamespace
           AND c.relkind IN ('r', 'p', 'v', 'm')
    LOOP
        EXECUTE o.cmd;   -- sequences "owned by" colunas acompanham a tabela
    END LOOP;

    FOR o IN
        SELECT format('ALTER FUNCTION %s OWNER TO migrator', p.oid::regprocedure) AS cmd
          FROM pg_proc p
         WHERE p.pronamespace = 'public'::regnamespace
           AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = p.oid AND d.deptype = 'e')  -- ignora funções de extensões
    LOOP
        EXECUTE o.cmd;
    END LOOP;
END
$$;

-- 4) Defaults: objetos futuros criados pelo migrator não vazam pra ninguém além dele.
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC;
