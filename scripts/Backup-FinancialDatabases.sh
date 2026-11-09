#!/usr/bin/env bash
set -euo pipefail
umask 077

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BACKUP_DIR="${FINANCIAL_BACKUP_ROOT:-/home/alexandre/financial-backups}"
METRICS_FILE="${FINANCIAL_BACKUP_METRICS_FILE:-$PROJECT_DIR/monitoring/metrics/financial_backup.prom}"
METRICS_DIR="$(dirname "$METRICS_FILE")"
RETENTION_DAYS=14
WRITE_CONTAINER="financial-write-db"
READ_CONTAINER="financial-read-db"
AGE_RECIPIENT="${FINANCIAL_BACKUP_AGE_RECIPIENT:-}"
ALLOW_PLAINTEXT="${FINANCIAL_BACKUP_ALLOW_PLAINTEXT:-}"
REMOTE_TARGET="${FINANCIAL_BACKUP_REMOTE_TARGET:-}"
RCLONE_CONFIG_PATH="${RCLONE_CONFIG:-}"

load_project_setting() {
  local setting_name="$1"
  if [[ -v "$setting_name" ]]; then
    printf '%s' "${!setting_name}"
    return
  fi
  if [[ -f "$PROJECT_DIR/.env" ]]; then
    awk -F= -v key="$setting_name" '$1 == key { sub(/^[^=]*=/, ""); sub(/\r$/, ""); print; exit }' "$PROJECT_DIR/.env"
  fi
}

AGE_RECIPIENT="$(load_project_setting FINANCIAL_BACKUP_AGE_RECIPIENT)"
ALLOW_PLAINTEXT="$(load_project_setting FINANCIAL_BACKUP_ALLOW_PLAINTEXT)"
REMOTE_TARGET="$(load_project_setting FINANCIAL_BACKUP_REMOTE_TARGET)"
RCLONE_CONFIG_PATH="$(load_project_setting RCLONE_CONFIG)"
if [[ -n "$RCLONE_CONFIG_PATH" ]]; then
  export RCLONE_CONFIG="$RCLONE_CONFIG_PATH"
fi

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
encryption_enabled=0
remote_copy_enabled=0
if [[ -n "$AGE_RECIPIENT" ]]; then encryption_enabled=1; fi
if [[ -n "$REMOTE_TARGET" ]]; then remote_copy_enabled=1; fi
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
    printf 'financial_backup_encryption_enabled %s\n' "$encryption_enabled"
    printf 'financial_backup_remote_copy_enabled %s\n' "$remote_copy_enabled"
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

if [[ -n "$AGE_RECIPIENT" && ! "$AGE_RECIPIENT" =~ ^age1[a-z0-9]+$ ]]; then
  echo "FINANCIAL_BACKUP_AGE_RECIPIENT deve conter uma chave pública age válida." >&2
  exit 2
fi
if [[ -n "$REMOTE_TARGET" && ! "$REMOTE_TARGET" =~ ^[A-Za-z0-9._-]+:[A-Za-z0-9._/-]*$ ]]; then
  echo "FINANCIAL_BACKUP_REMOTE_TARGET deve apontar para um remote rclone nomeado, como destino:financeiro/backups." >&2
  exit 2
fi
if [[ -n "$REMOTE_TARGET" && -z "$AGE_RECIPIENT" ]]; then
  echo "O envio externo exige FINANCIAL_BACKUP_AGE_RECIPIENT; dumps em claro não serão enviados." >&2
  exit 2
fi
if [[ "$ALLOW_PLAINTEXT" != "" && "$ALLOW_PLAINTEXT" != "true" && "$ALLOW_PLAINTEXT" != "false" ]]; then
  echo "FINANCIAL_BACKUP_ALLOW_PLAINTEXT deve ser true ou false." >&2
  exit 2
fi
if [[ -z "$AGE_RECIPIENT" && "$ALLOW_PLAINTEXT" != "true" ]]; then
  echo "Backup em claro bloqueado. Configure FINANCIAL_BACKUP_AGE_RECIPIENT ou autorize explicitamente o sandbox com FINANCIAL_BACKUP_ALLOW_PLAINTEXT=true." >&2
  exit 2
fi
if [[ -z "$AGE_RECIPIENT" ]]; then
  echo "AVISO: gravando dumps financeiros sem cifragem; use somente no sandbox isolado." >&2
fi

required_commands=(docker flock find sha256sum stat awk cut)
if [[ -n "$AGE_RECIPIENT" ]]; then
  required_commands+=(age)
fi
if [[ -n "$REMOTE_TARGET" ]]; then
  required_commands+=(rclone)
fi
for command in "${required_commands[@]}"; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Comando necessário não encontrado: $command" >&2
    exit 1
  }
done

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
  if [[ -n "$AGE_RECIPIENT" ]]; then
    printf 'encryption=age\n'
  else
    printf 'encryption=none\n'
  fi
  if [[ -n "$REMOTE_TARGET" ]]; then
    printf 'remote_copy=enabled\n'
  else
    printf 'remote_copy=disabled\n'
  fi
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
    artifact_file="$dump_file"
    artifact_name="${container}/${database}"
    if [[ -n "$AGE_RECIPIENT" ]]; then
      artifact_file="${dump_file}.age"
      docker exec "$container" pg_dump -U financial --format=custom --no-owner --no-acl --dbname="$database" |
        age --recipient "$AGE_RECIPIENT" --output "$artifact_file"
      artifact_name="${artifact_name}.age"
    else
      docker exec "$container" pg_dump -U financial --format=custom --no-owner --no-acl --dbname="$database" > "$dump_file"
      if [[ ! -s "$dump_file" ]]; then
        echo "Dump vazio: $container/$database" >&2
        exit 1
      fi
      docker exec -i "$container" pg_restore --list < "$dump_file" >/dev/null
    fi
    if [[ ! -s "$artifact_file" ]]; then
      echo "Arquivo de backup vazio: $artifact_file" >&2
      exit 1
    fi
    chmod 600 "$artifact_file"
    printf '%s bytes=%s sha256=' "$artifact_name" "$(stat -c '%s' "$artifact_file")" >> "$manifest"
    sha256sum "$artifact_file" | cut -d ' ' -f 1 >> "$manifest"
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

if [[ -n "$REMOTE_TARGET" ]]; then
  remote_backup_directory="${REMOTE_TARGET%/}/daily-$backup_id"
  rclone copy --checksum --transfers 4 "$final_directory" "$remote_backup_directory"
  rclone check --download --one-way "$final_directory" "$remote_backup_directory"
  echo "Cópia externa cifrada verificada: $remote_backup_directory."
fi

find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -name 'daily-*' -mtime "+$RETENTION_DAYS" -exec rm -rf -- {} +

last_success="$(date +%s)"
write_metrics 1 "$last_success"
echo "Backup concluído: $final_directory; bancos=$database_count; retenção=${RETENTION_DAYS}d."
