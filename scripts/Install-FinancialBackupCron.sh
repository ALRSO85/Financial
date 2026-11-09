#!/usr/bin/env bash
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BACKUP_SCRIPT="$PROJECT_DIR/scripts/Backup-FinancialDatabases.sh"
BACKUP_DIR="/home/alexandre/financial-backups"
LOG_FILE="$BACKUP_DIR/backup-cron.log"
JOB="0 3 * * * $BACKUP_SCRIPT >> $LOG_FILE 2>&1"

if [[ ! -x "$BACKUP_SCRIPT" ]]; then
  echo "Script de backup ausente ou sem permissão de execução: $BACKUP_SCRIPT" >&2
  exit 1
fi
if ! command -v crontab >/dev/null 2>&1; then
  echo "O comando crontab não está disponível." >&2
  exit 1
fi

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
temporary_crontab="$(mktemp)"
temporary_filtered="$(mktemp)"
trap 'rm -f -- "$temporary_crontab" "$temporary_filtered"' EXIT
crontab -l > "$temporary_crontab" 2>/dev/null || true
grep -Fv "$BACKUP_SCRIPT" "$temporary_crontab" > "$temporary_filtered" || true
printf '%s\n' "$JOB" >> "$temporary_filtered"
crontab "$temporary_filtered"

echo "Backup diário configurado para 03:00 no fuso do servidor."
