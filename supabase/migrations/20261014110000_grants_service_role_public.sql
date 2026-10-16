-- =========================================================
-- service_role precisa de acesso total ao schema public (os workers .NET usam a
-- service_role pelo PostgREST). Projetos/branches novos do Supabase vêm com default
-- privileges endurecidos (só Dxtm), então tabelas criadas pelas migrations ficavam sem
-- SELECT/INSERT/UPDATE/DELETE e o PostgREST respondia 403 (42501). Em produção já está
-- concedido, então é no-op lá. Idempotente. NÃO concede nada a anon/authenticated (US #149).
-- =========================================================
GRANT USAGE ON SCHEMA public TO service_role;
GRANT ALL ON ALL TABLES IN SCHEMA public TO service_role;
GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO service_role;
GRANT ALL ON ALL FUNCTIONS IN SCHEMA public TO service_role;

ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO service_role;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO service_role;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON FUNCTIONS TO service_role;
