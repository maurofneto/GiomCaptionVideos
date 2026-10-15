# Runbook — banco por ambiente (Supabase)

Atualizado em 15/10/2026 (US #149, PR 107).

## Onde cada ambiente grava

| Ambiente | Banco | Ref / URL | Como é criado |
|---|---|---|---|
| prod | Projeto Supabase `instituto-giom-video-manager` | `rhjsjfbegtusucunkgav` — `https://rhjsjfbegtusucunkgav.supabase.co` | Já existe; migrations pelo estágio `Migrations` do pipeline (`supabase db push`) |
| dev | **Branch `dev` do mesmo projeto** (Supabase Branching, sem dados) | `lusfwbiiuxyzduniptes` — `https://lusfwbiiuxyzduniptes.supabase.co` | `supabase branches create dev --project-ref rhjsjfbegtusucunkgav` |

O Postgres + PostgREST do Railway (`postgres-giom-caption-videos`) **não é mais usado por dev**.
Foi abandonado porque o PostgREST de dev aceitava leitura/escrita sem credencial e não recebia
migrations do pipeline (US #149). Produção nunca foi migrada para o Railway: o commit de corte
(`de90fe6`) existe só na branch local `feature/migracao-supabase-postgrest-railway` e **não deve**
ser promovido.

## Credenciais (SSM)

As três `SecretKey` de dev guardam a **service_role da branch dev** (SecureString):

- `/giom/dev/agenteinstituto/Supabase/SecretKey`
- `/giom/dev/canais/Supabase/SecretKey`
- `/giom/dev/pipeline/Supabase/SecretKey`

Prod continua em `/giom/prod/.../Supabase/SecretKey`. Nunca commitar o valor.

Obter a chave da branch sem imprimir:
`supabase branches get dev --project-ref rhjsjfbegtusucunkgav -o env` (variável
`SUPABASE_SERVICE_ROLE_KEY`) e gravar com `aws ssm put-parameter --type SecureString --overwrite`.
Se a branch for recriada, a chave muda: regrave as três.

## Migrations numa branch nova

O pooler (`POSTGRES_URL` do `-o env`) funciona; o host direto (`db.<ref>.supabase.co`) é só IPv6.

```bash
u=$(supabase branches get dev --project-ref rhjsjfbegtusucunkgav -o env | grep '^POSTGRES_URL=' | cut -d= -f2- | tr -d '"')
supabase db push --db-url "$u" --dry-run   # confira a lista
supabase db push --db-url "$u" --yes
```

Armadilha conhecida: a criação da branch copia o histórico de migrations de produção, mas as
primeiras podem constar como aplicadas sem terem criado as tabelas. Se `db push` falhar com
`relation "video" does not exist`, apague as linhas de `supabase_migrations.schema_migrations`
da **branch** (nunca da produção) e rode o push de novo.

`chat_sessao`, `conversa_historico` e `interacao_log` foram criadas fora de migration em produção.
A migration `20260706195336` agora traz o baseline idempotente dessas tabelas (no-op em prod).

## Branch é descartável; produção não

A branch dev pode ser resetada/recriada (`reset_branch`, `delete_branch`). Tem custo por hora de
compute enquanto existir. Dados de dev **não** vêm de produção (`with_data: false`); os seeds são
os das migrations (2 canais, configs de provedor, preços).

## Promoção para produção

Migrations novas entram em dev (branch) pelo pipeline e chegam a prod só pelo PR `dev` → `master`.
Antes: ver `docs/ENGINEERING_STANDARDS.md` (seção Migrations) e aplicar em janela as que travam
tabela (ex.: `halfvec`, PR 104).
