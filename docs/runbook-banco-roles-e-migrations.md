# Runbook — acesso ao banco por componente + migrations no pipeline (US #149)

## Por que existe
No Postgres de DEV (Railway) o PostgREST conectava como o superusuário `postgres` e usava essa mesma
role como papel **anônimo**: qualquer pessoa, sem credencial, lia e escrevia em tudo. Os workers
gravavam como anônimos (a `SecretKey` do SSM era o JWT antigo do Supabase, rejeitado pelo Railway).
Além disso o estágio `Migrations` só falava com projeto Supabase — o Railway nunca recebia migrations.

## Modelo
| Peça | Regra |
|---|---|
| Conexão do PostgREST | role `authenticator` (LOGIN, **sem** superusuário) — `PGRST_DB_URI` |
| Anônimo | **desligado**: sem `PGRST_DB_ANON_ROLE` (requisição sem JWT → 401) |
| JWT | HS256, assinado com `PGRST_JWT_SECRET` (≥ 32 chars), claim `role` = role do componente |
| Roles (NOLOGIN) | `pipeline_worker` (6 workers de etapa), `agente_bot`, `reconexao_leitura` |
| Grants | só tabelas/verbos que o código usa (migration `20261014130000`) + 1 policy de RLS por role/tabela |
| Migrations | role `migrator` (dona dos objetos, sem superusuário), aplicada pelo pipeline |

### Onde cada JWT vive (um por componente — independentes e rotacionáveis separadamente)
| Role | Consumidor | Destino |
|---|---|---|
| `pipeline_worker` | Ingestão/Transcrição/Resumo/Categorização/Embedding/Orquestração | SSM `/giom/{env}/pipeline/Supabase/SecretKey` **e** `/giom/{env}/canais/Supabase/SecretKey` |
| `agente_bot` | AgenteInstituto | SSM `/giom/{env}/agenteinstituto/Supabase/SecretKey` |
| `reconexao_leitura` | página `web/reconexao` (Vercel) | env var do projeto Vercel (ver `web/reconexao/README.md`) |

> Trocar o `PGRST_JWT_SECRET` invalida **todos** os JWTs: regenere os três e atualize os destinos acima.

## Implantação (DEV) — a ordem importa
1. **Migration** (cria roles/grants/policies; ainda como superusuário, PostgREST segue funcionando).
2. **Bootstrap** `scripts/db/bootstrap-railway.sql` (senhas por `-v`, nunca em arquivo).
3. **Segredos novos**: `PGRST_JWT_SECRET` (aleatório ≥ 32) e a senha do `authenticator`, definidos como variáveis do
   serviço `postgrest` no Railway **sem passar por tela/log** (via API/CLI com token no SSM).
4. **JWTs**: `PGRST_JWT_SECRET=… python scripts/db/mint-jwt.py --role <role> --days 365`, gravando direto no destino
   (SSM `SecureString`) — nunca imprimir.
5. **Só então** trocar o `postgrest`: `PGRST_DB_URI` com `authenticator`, **remover** `PGRST_DB_ANON_ROLE`, definir
   `PGRST_JWT_SECRET` e redeploy. (Antes disso os workers ainda funcionam como anônimos; depois, só com JWT.)
6. **Baseline do histórico** (o Railway nasceu de dump, sem `supabase_migrations`):
   `DATABASE_URL_MIGRATOR=… scripts/db/baseline-migrations.sh 20261014130000`.
7. **Variable group `giom-dev`**: `DB_ENGINE=postgres` e `DATABASE_URL_MIGRATOR` (**secreta**, `sslmode=require`,
   via TCP proxy do Railway). Prod continua `supabase` até migrar.

## Verificação (critérios de aceite)
```bash
URL=https://postgrest-development-59c5.up.railway.app
curl -s -o /dev/null -w "%{http_code}\n" "$URL/video?limit=1"                       # sem JWT  -> 401
curl -s -o /dev/null -w "%{http_code}\n" -X PATCH "$URL/video?id=eq.x" -d '{}' -H 'Content-Type: application/json'   # -> 401
# com o JWT de cada role: só o previsto (ex.: reconexao_leitura lê 4 colunas de canal_ingestao;
# agente_bot NÃO escreve em etapa_provedor_config -> 403/42501)
```
Depois rodar a pipeline dev ponta a ponta (ingestão → transcrição → resumo → categorização → embedding).

## Rotação
Anual (ou ao suspeitar de vazamento): novo `PGRST_JWT_SECRET` → reminte os 3 JWTs → atualize SSM/Vercel → redeploy
do `postgrest`. Tokens antigos morrem junto com o segredo antigo. Prazo de validade do JWT: 365 dias (`--days`).

## Rollback
- PostgREST: recolocar a variável anterior (`PGRST_DB_URI` do superusuário + anon) — **reabre o acesso anônimo**,
  só como emergência e por tempo curto.
- Migration: `DROP POLICY <role>_acesso ON <tabela>` e `REVOKE` dos grants da role (as roles novas podem ficar).

## Convenção para migrations novas
Devem funcionar em **Supabase e Postgres puro**: nada que dependa de `service_role`/`authenticated`/extensões do
Supabase sem guarda por existência (`IF EXISTS (SELECT 1 FROM pg_roles ...)`), e sempre idempotentes. Tabela nova =
grants explícitos para as roles que a usam (+ policy se o RLS estiver ligado).
