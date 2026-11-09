#!/usr/bin/env bash
set -euo pipefail
umask 077

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WRITE_CONTAINER="${FINANCIAL_WRITE_CONTAINER:-financial-write-db}"
SOURCE_DATABASE="${FINANCIAL_BACKUP_ENCRYPTION_TEST_DATABASE:-financial_write_instrumented}"
TEST_DIRECTORY="$(mktemp -d "${TMPDIR:-/tmp}/financial-backup-encryption.XXXXXX")"
chmod 700 "$TEST_DIRECTORY"
cleanup() {
  local result=$?
  trap - EXIT
  if [[ -n "$TEST_DIRECTORY" && -d "$TEST_DIRECTORY" ]]; then
    rm -rf -- "$TEST_DIRECTORY"
  fi
  exit "$result"
}
trap cleanup EXIT

for command in age age-keygen docker; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Comando necessário não encontrado: $command" >&2
    exit 1
  }
done
if [[ ! "$SOURCE_DATABASE" =~ ^financial[a-zA-Z0-9_]*$ || "$SOURCE_DATABASE" == financial_restore_validation_* ]]; then
  echo "Banco de ensaio fora do formato permitido: $SOURCE_DATABASE" >&2
  exit 2
fi
if [[ "$(docker exec "$WRITE_CONTAINER" psql -U financial -d postgres -Atqc \
    "SELECT count(*) FROM pg_database WHERE datname='$SOURCE_DATABASE'")" != "1" ]]; then
  echo "Banco de ensaio não encontrado no container $WRITE_CONTAINER: $SOURCE_DATABASE" >&2
  exit 2
fi

identity_file="$TEST_DIRECTORY/age-identity.txt"
age-keygen -o "$identity_file" >/dev/null 2>&1
chmod 600 "$identity_file"
recipient="$(age-keygen -y "$identity_file")"

backup_root="$TEST_DIRECTORY/backups"
metrics_file="$TEST_DIRECTORY/financial_backup.prom"
FINANCIAL_BACKUP_ROOT="$backup_root" \
FINANCIAL_BACKUP_METRICS_FILE="$metrics_file" \
FINANCIAL_BACKUP_AGE_RECIPIENT="$recipient" \
FINANCIAL_BACKUP_REMOTE_TARGET='' \
RCLONE_CONFIG='' \
  bash "$PROJECT_DIR/scripts/Backup-FinancialDatabases.sh"

backup_directory="$(find "$backup_root" -mindepth 1 -maxdepth 1 -type d -name 'daily-*' -print -quit)"
if [[ -z "$backup_directory" || ! -s "$backup_directory/MANIFEST.txt" ]]; then
  echo "A execução cifrada não produziu um manifesto de backup." >&2
  exit 1
fi
if ! grep -qx 'encryption=age' "$backup_directory/MANIFEST.txt"; then
  echo "O manifesto não identifica a cifragem age." >&2
  exit 1
fi
if find "$backup_directory" -type f -name '*.dump' -print -quit | grep -q .; then
  echo "Foi encontrado dump em claro no backup cifrado." >&2
  exit 1
fi
if [[ "$(awk '$1 == "financial_backup_encryption_enabled" {print $2}' "$metrics_file")" != "1" ]]; then
  echo "A métrica não indica cifragem ativa no ensaio." >&2
  exit 1
fi

FINANCIAL_BACKUP_ROOT="$backup_root" \
FINANCIAL_BACKUP_AGE_IDENTITY_FILE="$identity_file" \
FINANCIAL_RESTORE_SOURCE_DATABASE="$SOURCE_DATABASE" \
  bash "$PROJECT_DIR/scripts/Test-FinancialBackupArchive.sh" "$backup_directory"

echo "Backup age cifrado, checksum e restore foram validados; artefatos temporários removidos."
