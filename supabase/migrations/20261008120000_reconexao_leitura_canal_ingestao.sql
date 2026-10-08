-- =========================================================
-- Role de leitura restrita para a página de reconexão por canal (web/reconexao, PR-03).
-- A página lista os canais ativos de canal_ingestao via PostgREST usando um JWT com
-- role = reconexao_leitura. Essa role só enxerga channel_id/nome/secrets_key/ativo das linhas
-- ativas — nada de playlist_id, regras de relevância, limites etc., e nenhuma outra tabela.
--
-- Idempotente (o pipeline roda `supabase db push` a cada merge) e NÃO altera o acesso de
-- nenhuma outra role: só cria/ajusta grants da própria reconexao_leitura e adiciona uma
-- policy nova, restrita (TO reconexao_leitura). A policy "service_role_only_canal_ingestao"
-- existente não é tocada.
--
-- O JWT dessa role NÃO é gerado aqui (depende do JWT secret do projeto): ver
-- web/reconexao/README.md, seção "JWT da role reconexao_leitura".
-- =========================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'reconexao_leitura') THEN
        CREATE ROLE reconexao_leitura NOLOGIN NOINHERIT;
    END IF;

    -- authenticator é a role de conexão do PostgREST; precisa poder assumir a role do JWT.
    -- Guardado por existência/membership para ser reaplicável (e para ambientes sem Supabase).
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticator')
       AND NOT pg_has_role('authenticator', 'reconexao_leitura', 'MEMBER') THEN
        GRANT reconexao_leitura TO authenticator;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO reconexao_leitura;

-- Zera qualquer grant prévio DESTA role na tabela (re-execução converge) e concede só as 4 colunas.
REVOKE ALL ON TABLE canal_ingestao FROM reconexao_leitura;
GRANT SELECT (channel_id, nome, secrets_key, ativo) ON TABLE canal_ingestao TO reconexao_leitura;

-- RLS já está habilitado em canal_ingestao (migration 20260710151723). Policies permissivas se
-- somam por role: esta só vale para reconexao_leitura e só deixa ler canais ativos.
DROP POLICY IF EXISTS "reconexao_leitura_canais_ativos" ON canal_ingestao;
CREATE POLICY "reconexao_leitura_canais_ativos" ON canal_ingestao
    FOR SELECT
    TO reconexao_leitura
    USING (ativo = true);
