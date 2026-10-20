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

## Grants do service_role em branch nova

Branches/projetos novos do Supabase vêm com default privileges endurecidos: tabelas criadas pelas
migrations ficam sem SELECT/INSERT/UPDATE/DELETE para `service_role` e o PostgREST responde
`403` / `42501 permission denied`. A migration `20261014110000_grants_service_role_public` concede
o acesso (no-op em produção) e **não** concede nada a `anon`/`authenticated`. Teste de sanidade
depois de qualquer recriação da branch: com a service_role, `GET /rest/v1/canal_ingestao` deve dar
200; sem chave, 401.

## Pendências conhecidas (15/10/2026)

- **Pipeline (`azure-pipelines.yml`, stage `Migrations`)**: o variable group `giom-dev` já aponta `SUPABASE_PROJECT_REF` para `lusfwbiiuxyzduniptes` (antes: `fajrstyqgogmqzceujjt`, projeto antigo de dev). O `supabase link` + `db push` do pipeline contra a branch ainda **não foi exercitado**: confira o primeiro run após o merge do PR 107.
- **Imagens de dev já publicadas** ainda têm a URL do Railway no `appsettings.Development.json`;
  só passam a usar a branch depois do merge do PR 107 e do deploy. Para testar antes, use
  override de ambiente na task: `Supabase__Url`, `Supabase__RestPrefix=/rest/v1`.
- **Credencial Google de dev**: os refresh tokens dos 2 canais estão em `invalid_grant`; a
  ingestão em dev lê os canais do banco mas pula os dois até reconectar pela página de reconexão
  (US #146).
- **PRs 104 e 105** (halfvec e filtros de canal no RAG) ainda precisam ser aplicados na branch.

## Histórico fora de ordem (`--include-all`)

O `supabase db push` recusa aplicar uma migration cuja versão é anterior à última registrada no remoto ("Found local migration files to be inserted before the last migration"). Isso acontece depois de um `migration repair --status applied` de uma versão nova ou de uma migration aplicada à mão. O estágio `Migrations` do `azure-pipelines.yml` usa `--include-all` para não travar; **o estágio falho não impede os Deploys seguintes**, então um histórico fora de ordem deixava código novo com schema antigo (promoção de 16/10/2026, US #152). Ao registrar uma migration como aplicada, confira antes com `supabase db push --include-all --dry-run` o que ainda está pendente.
