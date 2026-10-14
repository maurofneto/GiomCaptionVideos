#!/usr/bin/env bash
# Baseline do histórico de migrations num Postgres que NÃO nasceu das migrations (ex.: Railway,
# montado por dump) — US #149. Marca como JÁ APLICADAS todas as migrations locais com versão <= a
# informada, sem executá-las. Sem isso, `supabase db push --db-url` tentaria reaplicar tudo do zero.
#
#   DATABASE_URL_MIGRATOR=postgresql://migrator:...@host:port/db?sslmode=require \
#     scripts/db/baseline-migrations.sh 20261014130000
#
# Só rodar depois de CONFERIR que o banco já contém o efeito dessas migrations. Idempotente.
set -euo pipefail
ate="${1:?uso: baseline-migrations.sh <versao-limite, ex.: 20261014130000>}"
: "${DATABASE_URL_MIGRATOR:?defina DATABASE_URL_MIGRATOR}"

versoes=()
for f in supabase/migrations/*.sql; do
  v="$(basename "$f" | cut -d_ -f1)"
  [ "$v" -le "$ate" ] && versoes+=("$v")
done
echo "Marcando ${#versoes[@]} migrations como aplicadas (<= $ate)..."
supabase migration repair --db-url "$DATABASE_URL_MIGRATOR" --status applied "${versoes[@]}"
supabase migration list --db-url "$DATABASE_URL_MIGRATOR" | tail -5
