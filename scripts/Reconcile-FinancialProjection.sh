#!/usr/bin/env bash
set -euo pipefail
umask 077

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
for command in docker mktemp cmp comm wc tr sleep; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Comando necessário não encontrado: $command" >&2
    exit 1
  }
done

temporary_directory="$(mktemp -d)"
trap 'rm -rf -- "$temporary_directory"' EXIT

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
  printf 'Divergência de eventos maduros: ids divergentes=%s; processados=%s; projetados=%s\n' \
    "$event_id_differences" "$write_mature_count" "$read_mature_count" >&2
  exit 1
fi

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
