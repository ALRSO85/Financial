#!/usr/bin/env bash
set -euo pipefail
umask 077

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
METRICS_DIR="$PROJECT_DIR/monitoring/metrics"
METRICS_FILE="$METRICS_DIR/financial_reconciliation.prom"
LOCK_FILE="$METRICS_DIR/.financial_reconciliation.lock"
WRITE_CONTAINER="${FINANCIAL_WRITE_CONTAINER:-financial-write-db}"
READ_CONTAINER="${FINANCIAL_READ_CONTAINER:-financial-read-db}"
WRITE_DATABASE="${FINANCIAL_WRITE_DATABASE:-financial}"
READ_DATABASE="${FINANCIAL_READ_DATABASE:-financial_read}"
SETTLE_SECONDS="${FINANCIAL_RECONCILIATION_SETTLE_SECONDS:-300}"
MAX_PENDING_AGE_SECONDS="${FINANCIAL_RECONCILIATION_MAX_PENDING_AGE_SECONDS:-300}"
RETRIES="${FINANCIAL_RECONCILIATION_RETRIES:-3}"
RETRY_DELAY_SECONDS="${FINANCIAL_RECONCILIATION_RETRY_DELAY_SECONDS:-2}"

for database in "$WRITE_DATABASE" "$READ_DATABASE"; do
  if [[ ! "$database" =~ ^financial[a-zA-Z0-9_]*$ || "$database" == financial_restore_validation_* ]]; then
    echo "Banco fora do formato permitido: $database" >&2
    exit 2
  fi
done
for value in "$SETTLE_SECONDS" "$MAX_PENDING_AGE_SECONDS" "$RETRIES" "$RETRY_DELAY_SECONDS"; do
  if [[ ! "$value" =~ ^(0|[1-9][0-9]{0,5})$ ]]; then
    echo "Os limites de reconciliação devem ser inteiros não negativos." >&2
    exit 2
  fi
done
if (( SETTLE_SECONDS == 0 || SETTLE_SECONDS > 86400 || MAX_PENDING_AGE_SECONDS > 604800 ||
      RETRIES == 0 || RETRIES > 10 || RETRY_DELAY_SECONDS > 60 )); then
  echo "Limites fora do intervalo aceito (settle: 1-86400s, backlog: 0-604800s, tentativas: 1-10, espera: 0-60s)." >&2
  exit 2
fi
for command in docker flock mktemp cmp comm wc tr sleep; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Comando necessário não encontrado: $command" >&2
    exit 1
  }
done

mkdir -p "$METRICS_DIR"
exec 9>"$LOCK_FILE"
if ! flock -n 9; then
  echo "Outra reconciliação já está em execução; encerrando sem sobrescrever as métricas."
  exit 0
fi

last_run_success=0
run_completed=0
ledger_mismatches=-1
projection_mismatches=-1
event_id_mismatches=-1
pending_count=-1
oldest_pending_age=-1
dead_letter_count=-1
last_success=""
if [[ -f "$METRICS_FILE" ]]; then
  last_success="$(awk '$1 == "financial_reconciliation_last_success_timestamp_seconds" {print $2; exit}' "$METRICS_FILE")"
fi
if [[ ! "$last_success" =~ ^[0-9]+([.][0-9]+)?$ ]]; then
  last_success=""
fi

temporary_directory="$(mktemp -d)"
write_metrics() {
  local temporary_metrics timestamp
  timestamp="$(date +%s)"
  temporary_metrics="$(mktemp "$METRICS_DIR/.financial_reconciliation.XXXXXX")"
  {
    printf 'financial_reconciliation_last_run_success %s\n' "$last_run_success"
    printf 'financial_reconciliation_run_completed %s\n' "$run_completed"
    printf 'financial_reconciliation_last_run_timestamp_seconds %s\n' "$timestamp"
    printf 'financial_reconciliation_ledger_account_mismatches %s\n' "$ledger_mismatches"
    printf 'financial_reconciliation_projection_account_mismatches %s\n' "$projection_mismatches"
    printf 'financial_reconciliation_event_id_mismatches %s\n' "$event_id_mismatches"
    printf 'financial_reconciliation_outbox_pending %s\n' "$pending_count"
    printf 'financial_reconciliation_oldest_pending_age_seconds %s\n' "$oldest_pending_age"
    printf 'financial_reconciliation_dead_letter_events %s\n' "$dead_letter_count"
    if [[ -n "$last_success" ]]; then
      printf 'financial_reconciliation_last_success_timestamp_seconds %s\n' "$last_success"
    fi
  } > "$temporary_metrics"
  chmod 644 "$temporary_metrics"
  mv -f "$temporary_metrics" "$METRICS_FILE"
}

finish() {
  local result=$?
  trap - EXIT
  if (( result == 0 )); then
    last_run_success=1
    run_completed=1
    last_success="$(date +%s)"
  fi
  write_metrics || true
  rm -rf -- "$temporary_directory"
  exit "$result"
}
trap finish EXIT

write_mature_event_ids() {
  docker exec "$WRITE_CONTAINER" psql -U financial -d "$WRITE_DATABASE" -Atqc \
    "SELECT event_id::text FROM financial_outbox WHERE processed_at IS NOT NULL AND dead_lettered_at IS NULL AND processed_at < now() - make_interval(secs => $SETTLE_SECONDS) ORDER BY event_id"
}

read_mature_event_ids() {
  docker exec "$READ_CONTAINER" psql -U financial -d "$READ_DATABASE" -Atqc \
    "SELECT event_id::text FROM financial_projected_events WHERE projected_at < now() - make_interval(secs => $SETTLE_SECONDS) ORDER BY event_id"
}

snapshot_is_stable=0
for ((attempt = 1; attempt <= RETRIES; attempt++)); do
  write_mature_event_ids > "$temporary_directory/write-before"
  read_mature_event_ids > "$temporary_directory/read-before"
  sleep "$RETRY_DELAY_SECONDS"
  write_mature_event_ids > "$temporary_directory/write-after"
  read_mature_event_ids > "$temporary_directory/read-after"

  if cmp -s "$temporary_directory/write-before" "$temporary_directory/write-after" &&
     cmp -s "$temporary_directory/read-before" "$temporary_directory/read-after"; then
    snapshot_is_stable=1
    break
  fi
done

if (( ! snapshot_is_stable )); then
  echo "Reconciliação inconclusiva: os conjuntos de eventos mudaram durante as $RETRIES tentativas." >&2
  exit 2
fi

ledger_mismatches="$(docker exec "$WRITE_CONTAINER" psql -U financial -d "$WRITE_DATABASE" -Atqc \
  "SELECT count(*) FROM financial_accounts account WHERE account.balance_cents <> COALESCE((SELECT sum(entry.signed_amount_cents) FROM financial_entries entry WHERE entry.account_id = account.id), 0)")"
projection_mismatches="$(docker exec "$READ_CONTAINER" psql -U financial -d "$READ_DATABASE" -Atqc \
  "SELECT count(*) FROM financial_read_accounts account WHERE account.balance_cents <> COALESCE((SELECT sum(entry.signed_amount_cents) FROM financial_read_entries entry WHERE entry.account_id = account.account_id), 0)")"
pending_count="$(docker exec "$WRITE_CONTAINER" psql -U financial -d "$WRITE_DATABASE" -Atqc \
  "SELECT count(*) FROM financial_outbox WHERE processed_at IS NULL AND dead_lettered_at IS NULL")"
dead_letter_count="$(docker exec "$WRITE_CONTAINER" psql -U financial -d "$WRITE_DATABASE" -Atqc \
  "SELECT count(*) FROM financial_outbox WHERE dead_lettered_at IS NOT NULL")"
oldest_pending_age="$(docker exec "$WRITE_CONTAINER" psql -U financial -d "$WRITE_DATABASE" -Atqc \
  "SELECT COALESCE(extract(epoch FROM now() - min(created_at))::bigint, 0) FROM financial_outbox WHERE processed_at IS NULL AND dead_lettered_at IS NULL")"
write_mature_count="$(wc -l < "$temporary_directory/write-after" | tr -d ' ')"
read_mature_count="$(wc -l < "$temporary_directory/read-after" | tr -d ' ')"

if [[ ! "$ledger_mismatches" =~ ^[0-9]+$ || ! "$projection_mismatches" =~ ^[0-9]+$ ||
      ! "$pending_count" =~ ^[0-9]+$ || ! "$dead_letter_count" =~ ^[0-9]+$ ||
      ! "$oldest_pending_age" =~ ^[0-9]+$ ]]; then
  echo "Resposta inesperada do PostgreSQL durante a reconciliação." >&2
  exit 2
fi

if ! cmp -s "$temporary_directory/write-after" "$temporary_directory/read-after"; then
  event_id_differences="$(comm -3 "$temporary_directory/write-after" "$temporary_directory/read-after" | wc -l | tr -d ' ')"
  event_id_mismatches="$event_id_differences"
  run_completed=1
  printf 'Divergência de eventos maduros: ids divergentes=%s; processados=%s; projetados=%s\n' \
    "$event_id_differences" "$write_mature_count" "$read_mature_count" >&2
  exit 1
fi
event_id_mismatches=0
run_completed=1

printf 'Reconciliação: ledger_contas_divergentes=%s; projeção_contas_divergentes=%s; eventos_maduros=%s; outbox_pendente=%s; idade_mais_antigo_pendente_s=%s; dead_letters=%s\n' \
  "$ledger_mismatches" "$projection_mismatches" "$write_mature_count" "$pending_count" \
  "$oldest_pending_age" "$dead_letter_count"

if (( ledger_mismatches != 0 || projection_mismatches != 0 || dead_letter_count != 0 )); then
  echo "Reconciliação encontrou inconsistência financeira ou evento em dead letter." >&2
  exit 1
fi
if (( pending_count != 0 && oldest_pending_age > MAX_PENDING_AGE_SECONDS )); then
  echo "A projeção está atrasada além do limite de $MAX_PENDING_AGE_SECONDS segundos." >&2
  exit 1
fi
