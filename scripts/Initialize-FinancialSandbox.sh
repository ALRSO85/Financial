#!/usr/bin/env bash
set -euo pipefail

ENV_FILE="${1:-.env}"
WRITE_CONTAINER="${FINANCIAL_WRITE_CONTAINER:-financial-write-db}"
READ_CONTAINER="${FINANCIAL_READ_CONTAINER:-financial-read-db}"

if ! command -v docker >/dev/null 2>&1 || ! command -v openssl >/dev/null 2>&1; then
  echo "Docker e OpenSSL são necessários." >&2
  exit 1
fi

write_state="$(docker exec "$WRITE_CONTAINER" psql -U financial -d postgres -Atqc "SELECT (SELECT count(*) FROM pg_roles WHERE rolname='financial_write_app') || '|' || (SELECT count(*) FROM pg_database WHERE datname='financial_write_monitoring')")"
read_state="$(docker exec "$READ_CONTAINER" psql -U financial -d postgres -Atqc "SELECT (SELECT count(*) FROM pg_roles WHERE rolname='financial_read_app') || '|' || (SELECT count(*) FROM pg_database WHERE datname='financial_read_monitoring')")"

if [[ "$write_state" != "0|0" || "$read_state" != "0|0" ]]; then
  echo "Há provisionamento existente ou parcial; nenhuma senha foi rotacionada e nenhum banco foi alterado." >&2
  printf 'Estado escrita=%s leitura=%s\n' "$write_state" "$read_state" >&2
  exit 2
fi

if [[ ! -f "$ENV_FILE" ]]; then
  install -m 600 /dev/null "$ENV_FILE"
fi
chmod 600 "$ENV_FILE"

ensure_secret() {
  local key="$1"
  local current
  current="$(sed -n "s/^${key}=//p" "$ENV_FILE" | head -n 1)"
  if [[ -z "$current" ]]; then
    printf '%s=%s\n' "$key" "$(openssl rand -hex 32)" >> "$ENV_FILE"
  elif [[ "$current" == replace-with-* ]]; then
    sed -i "s/^${key}=.*/${key}=$(openssl rand -hex 32)/" "$ENV_FILE"
  fi
}

ensure_setting() {
  local key="$1"
  local value="$2"
  if ! grep -q "^${key}=" "$ENV_FILE"; then
    printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
  fi
}

ensure_secret FINANCIAL_JWT_SIGNING_KEY
ensure_secret FINANCIAL_WRITE_DB_PASSWORD
ensure_secret FINANCIAL_READ_DB_PASSWORD
ensure_setting FINANCIAL_DATABASE_NETWORK financial-db_default
ensure_setting MONITORING_HOST_ADDRESS host-gateway

read_env_value() {
  local key="$1"
  sed -n "s/^${key}=//p" "$ENV_FILE" | head -n 1
}

FINANCIAL_JWT_SIGNING_KEY="$(read_env_value FINANCIAL_JWT_SIGNING_KEY)"
FINANCIAL_WRITE_DB_PASSWORD="$(read_env_value FINANCIAL_WRITE_DB_PASSWORD)"
FINANCIAL_READ_DB_PASSWORD="$(read_env_value FINANCIAL_READ_DB_PASSWORD)"
if [[ ! "$FINANCIAL_JWT_SIGNING_KEY" =~ ^[a-f0-9]{64}$ || ! "$FINANCIAL_WRITE_DB_PASSWORD" =~ ^[a-f0-9]{64}$ || ! "$FINANCIAL_READ_DB_PASSWORD" =~ ^[a-f0-9]{64}$ ]]; then
  echo "Segredo do sandbox ausente ou inválido em $ENV_FILE." >&2
  exit 1
fi

temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT
umask 077
printf "CREATE ROLE financial_write_app LOGIN PASSWORD '%s';\nCREATE DATABASE financial_write_monitoring OWNER financial_write_app;\n" "$FINANCIAL_WRITE_DB_PASSWORD" > "$temp_dir/write.sql"
printf "CREATE ROLE financial_read_app LOGIN PASSWORD '%s';\nCREATE DATABASE financial_read_monitoring OWNER financial_read_app;\n" "$FINANCIAL_READ_DB_PASSWORD" > "$temp_dir/read.sql"

docker exec -i "$WRITE_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d postgres < "$temp_dir/write.sql"
docker exec -i "$READ_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d postgres < "$temp_dir/read.sql"

echo "Bancos dedicados provisionados. Segredos foram gravados somente em $ENV_FILE (permissão 600)."
