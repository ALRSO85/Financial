#!/usr/bin/env bash
set -euo pipefail
umask 077

BACKUP_ROOT="${FINANCIAL_BACKUP_ROOT:-/home/alexandre/financial-backups}"
WRITE_CONTAINER="${FINANCIAL_WRITE_CONTAINER:-financial-write-db}"
SOURCE_DATABASE="${FINANCIAL_RESTORE_SOURCE_DATABASE:-financial_write_instrumented}"

if [[ ! "$SOURCE_DATABASE" =~ ^financial[a-zA-Z0-9_]*$ || "$SOURCE_DATABASE" == financial_restore_validation_* ]]; then
  echo "Banco de origem fora do formato permitido: $SOURCE_DATABASE" >&2
  exit 2
fi

backup_directory="${1:-}"
if [[ -z "$backup_directory" ]]; then
  backup_directory="$(find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d -name 'daily-*' -printf '%f\n' | sort | tail -n 1)"
  backup_directory="$BACKUP_ROOT/$backup_directory"
fi

dump_file="$backup_directory/${WRITE_CONTAINER}_${SOURCE_DATABASE}.dump"
if [[ ! -s "$dump_file" ]]; then
  echo "Dump não encontrado ou vazio: $dump_file" >&2
  exit 2
fi
manifest_file="$backup_directory/MANIFEST.txt"
if [[ ! -s "$manifest_file" ]]; then
  echo "Manifesto não encontrado ou vazio: $manifest_file" >&2
  exit 2
fi

for command in docker sha256sum; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Comando necessário não encontrado: $command" >&2
    exit 1
  }
done

restore_database="financial_restore_validation_$(date -u +%Y%m%dT%H%M%SZ)_$$"
expected_sha256="$(awk -v artifact="$WRITE_CONTAINER/$SOURCE_DATABASE" '$1 == artifact { sub(/^sha256=/, "", $3); print $3; exit }' "$manifest_file")"
actual_sha256="$(sha256sum "$dump_file" | cut -d ' ' -f 1)"
if [[ ! "$expected_sha256" =~ ^[a-f0-9]{64}$ || "$actual_sha256" != "$expected_sha256" ]]; then
  echo "SHA-256 não corresponde ao manifesto para $dump_file" >&2
  exit 1
fi

database_created=0
cleanup() {
  local result=$?
  trap - EXIT
  if (( database_created )); then
    docker exec "$WRITE_CONTAINER" dropdb -U financial --if-exists "$restore_database" >/dev/null 2>&1 || true
  fi
  exit "$result"
}
trap cleanup EXIT

docker exec -i "$WRITE_CONTAINER" pg_restore --list < "$dump_file" >/dev/null
docker exec "$WRITE_CONTAINER" createdb -U financial "$restore_database"
database_created=1
docker exec -i "$WRITE_CONTAINER" pg_restore -U financial --dbname="$restore_database" --no-owner --no-acl < "$dump_file"

query="SELECT (SELECT count(*) FROM financial_accounts)||chr(124)||(SELECT count(*) FROM financial_entries)||chr(124)||(SELECT COALESCE(sum(balance_cents),0) FROM financial_accounts)||chr(124)||(SELECT COALESCE(sum(signed_amount_cents),0) FROM financial_entries)||chr(124)||(SELECT count(*) FROM financial_outbox)||chr(124)||(SELECT count(*) FROM financial_audit_events)||chr(124)||(SELECT count(*) FROM financial_schema_migrations)"
source_result="$(docker exec "$WRITE_CONTAINER" psql -U financial -d "$SOURCE_DATABASE" -Atqc "$query")"
restored_result="$(docker exec "$WRITE_CONTAINER" psql -U financial -d "$restore_database" -Atqc "$query")"

if [[ "$source_result" != "$restored_result" ]]; then
  printf 'Divergência na restauração. Origem: %s; restaurado: %s\n' "$source_result" "$restored_result" >&2
  exit 1
fi

printf 'Restauração validada. origem=%s; banco_temporário=%s; sha256=%s; accounts|entries|balance_cents|entry_cents|outbox|audit|migrations=%s\n' \
  "$SOURCE_DATABASE" "$restore_database" "$actual_sha256" "$restored_result"
