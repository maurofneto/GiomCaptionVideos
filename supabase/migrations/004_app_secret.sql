-- =========================================================
-- Tabela de secrets para o SupabaseSecretsProvider (.NET)
-- Chaves no formato "Secao:Campo" (ex: "Claude:ApiKey"),
-- espelhando o caminho equivalente no appsettings.json.
-- =========================================================

CREATE TABLE IF NOT EXISTS app_secret (
    key         TEXT        PRIMARY KEY,
    value       TEXT        NOT NULL,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE app_secret ENABLE ROW LEVEL SECURITY;

-- Nunca exposto a anon/authenticated — somente o worker (service_role) acessa
CREATE POLICY "service_role_only_app_secret" ON app_secret FOR ALL USING (auth.role() = 'service_role');
