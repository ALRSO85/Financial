#!/usr/bin/env bash
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RECONCILIATION_SCRIPT="$PROJECT_DIR/scripts/Reconcile-FinancialProjection.sh"
BACKUP_DIR="/home/alexandre/financial-backups"
LOG_FILE="$BACKUP_DIR/financial-reconciliation-cron.log"
WRITE_DATABASE="${FINANCIAL_WRITE_DATABASE:-financial}"
READ_DATABASE="${FINANCIAL_READ_DATABASE:-financial_read}"

for database in "$WRITE_DATABASE" "$READ_DATABASE"; do
  if [[ ! "$database" =~ ^financial[a-zA-Z0-9_]*$ || "$database" == financial_restore_validation_* ]]; then
    echo "Banco fora do formato permitido: $database" >&2
    exit 2
  fi
done
if [[ ! -x "$RECONCILIATION_SCRIPT" ]]; then
  echo "Script de reconciliação ausente ou sem permissão de execução: $RECONCILIATION_SCRIPT" >&2
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
grep -Fv "$RECONCILIATION_SCRIPT" "$temporary_crontab" > "$temporary_filtered" || true
printf '*/5 * * * * FINANCIAL_WRITE_DATABASE=%s FINANCIAL_READ_DATABASE=%s "%s" >> "%s" 2>&1\n' \
  "$WRITE_DATABASE" "$READ_DATABASE" "$RECONCILIATION_SCRIPT" "$LOG_FILE" >> "$temporary_filtered"
crontab "$temporary_filtered"

echo "Reconciliação financeira agendada a cada 5 minutos: escrita=$WRITE_DATABASE; leitura=$READ_DATABASE."
