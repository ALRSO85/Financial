#!/usr/bin/env bash
set -euo pipefail
umask 077

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BACKUP_DIR="/home/alexandre/financial-backups"
METRICS_DIR="$PROJECT_DIR/monitoring/metrics"
METRICS_FILE="$METRICS_DIR/financial_backup.prom"
RETENTION_DAYS=14
WRITE_CONTAINER="financial-write-db"
READ_CONTAINER="financial-read-db"

for command in docker flock find sha256sum stat; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Comando necessário não encontrado: $command" >&2
    exit 1
  }
done

mkdir -p "$BACKUP_DIR" "$METRICS_DIR"
chmod 700 "$BACKUP_DIR"
exec 9>"$BACKUP_DIR/.backup.lock"
if ! flock -n 9; then
  echo "Outro backup já está em execução; encerrando sem criar cópia concorrente."
  exit 0
fi

last_success=""
if [[ -f "$METRICS_FILE" ]]; then
  last_success="$(awk '$1 == "financial_backup_last_success_timestamp_seconds" {print $2; exit}' "$METRICS_FILE")"
fi
database_count=0
temporary_directory=""

write_metrics() {
  local success="$1"
  local timestamp="$2"
  local temporary_metrics
  temporary_metrics="$(mktemp "$METRICS_DIR/.financial_backup.XXXXXX")"
  {
    printf 'financial_backup_last_run_success %s\n' "$success"
    printf 'financial_backup_last_run_timestamp_seconds %s\n' "$(date +%s)"
    printf 'financial_backup_database_count %s\n' "$database_count"
    if [[ -n "$timestamp" ]]; then
      printf 'financial_backup_last_success_timestamp_seconds %s\n' "$timestamp"
    fi
  } > "$temporary_metrics"
  chmod 644 "$temporary_metrics"
  mv -f "$temporary_metrics" "$METRICS_FILE"
}

cleanup() {
  local result=$?
  trap - EXIT
  if [[ -n "$temporary_directory" && -d "$temporary_directory" ]]; then
    rm -rf -- "$temporary_directory"
  fi
  if (( result != 0 )); then
    write_metrics 0 "$last_success" || true
    echo "Backup financeiro falhou com código $result." >&2
  fi
  exit "$result"
}
trap cleanup EXIT

for container in "$WRITE_CONTAINER" "$READ_CONTAINER"; do
  if [[ "$(docker inspect --format '{{.State.Running}}' "$container" 2>/dev/null || true)" != "true" ]]; then
    echo "Container PostgreSQL indisponível: $container" >&2
    exit 1
  fi
done

backup_id="$(date -u +%Y%m%dT%H%M%SZ)"
temporary_directory="$(mktemp -d "$BACKUP_DIR/.financial-backup-${backup_id}.XXXXXX")"
chmod 700 "$temporary_directory"

manifest="$temporary_directory/MANIFEST.txt"
{
  printf 'format=pg_dump-custom\n'
  printf 'created_at_utc=%s\n' "$(date -u +%FT%TZ)"
  printf 'write_container=%s\n' "$WRITE_CONTAINER"
  printf 'read_container=%s\n' "$READ_CONTAINER"
  printf 'retention_days=%s\n' "$RETENTION_DAYS"
} > "$manifest"

for container in "$WRITE_CONTAINER" "$READ_CONTAINER"; do
  mapfile -t databases < <(docker exec "$container" psql -U financial -d postgres -Atqc \
    "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate AND datname LIKE 'financial%' AND datname NOT LIKE 'financial_restore_validation_%' ORDER BY datname")
  if (( ${#databases[@]} == 0 )); then
    echo "Nenhum banco Financial encontrado em $container." >&2
    exit 1
  fi

  for database in "${databases[@]}"; do
    if [[ ! "$database" =~ ^financial[a-zA-Z0-9_]*$ ]]; then
      echo "Nome de banco fora do formato permitido: $database" >&2
      exit 1
    fi
    dump_file="$temporary_directory/${container}_${database}.dump"
    docker exec "$container" pg_dump -U financial --format=custom --no-owner --no-acl --dbname="$database" > "$dump_file"
    if [[ ! -s "$dump_file" ]]; then
      echo "Dump vazio: $container/$database" >&2
      exit 1
    fi
    docker exec -i "$container" pg_restore --list - < "$dump_file" >/dev/null
    chmod 600 "$dump_file"
    printf '%s/%s bytes=%s sha256=' "$container" "$database" "$(stat -c '%s' "$dump_file")" >> "$manifest"
    sha256sum "$dump_file" | cut -d ' ' -f 1 >> "$manifest"
    database_count=$((database_count + 1))
  done
done

chmod 600 "$manifest"
final_directory="$BACKUP_DIR/daily-$backup_id"
if [[ -e "$final_directory" ]]; then
  echo "Destino de backup já existe: $final_directory" >&2
  exit 1
fi
mv -- "$temporary_directory" "$final_directory"
temporary_directory=""
chmod 700 "$final_directory"

find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -name 'daily-*' -mtime "+$RETENTION_DAYS" -exec rm -rf -- {} +

last_success="$(date +%s)"
write_metrics 1 "$last_success"
echo "Backup concluído: $final_directory; bancos=$database_count; retenção=${RETENTION_DAYS}d."
