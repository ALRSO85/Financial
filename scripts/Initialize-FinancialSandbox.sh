#!/usr/bin/env bash
set -euo pipefail

ENV_FILE="${1:-.env}"
WRITE_CONTAINER="${FINANCIAL_WRITE_CONTAINER:-financial-write-db}"
READ_CONTAINER="${FINANCIAL_READ_CONTAINER:-financial-read-db}"

if ! command -v docker >/dev/null 2>&1 || ! command -v openssl >/dev/null 2>&1; then
  echo "Docker e OpenSSL são necessários." >&2
  exit 1
fi

write_state="$(docker exec "$WRITE_CONTAINER" psql -U financial -d postgres -Atqc "SELECT (SELECT count(*) FROM pg_roles WHERE rolname IN ('financial_write_app','financial_write_migrator')) || '|' || (SELECT count(*) FROM pg_database WHERE datname='financial_write_monitoring')")"
read_state="$(docker exec "$READ_CONTAINER" psql -U financial -d postgres -Atqc "SELECT (SELECT count(*) FROM pg_roles WHERE rolname IN ('financial_read_app','financial_read_migrator')) || '|' || (SELECT count(*) FROM pg_database WHERE datname='financial_read_monitoring')")"

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
ensure_secret FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD
ensure_secret FINANCIAL_READ_MIGRATOR_DB_PASSWORD
ensure_setting FINANCIAL_DATABASE_NETWORK financial-db_default
ensure_setting MONITORING_HOST_ADDRESS host-gateway

read_env_value() {
  local key="$1"
  sed -n "s/^${key}=//p" "$ENV_FILE" | head -n 1
}

FINANCIAL_JWT_SIGNING_KEY="$(read_env_value FINANCIAL_JWT_SIGNING_KEY)"
FINANCIAL_WRITE_DB_PASSWORD="$(read_env_value FINANCIAL_WRITE_DB_PASSWORD)"
FINANCIAL_READ_DB_PASSWORD="$(read_env_value FINANCIAL_READ_DB_PASSWORD)"
FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD="$(read_env_value FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD)"
FINANCIAL_READ_MIGRATOR_DB_PASSWORD="$(read_env_value FINANCIAL_READ_MIGRATOR_DB_PASSWORD)"
if [[ ! "$FINANCIAL_JWT_SIGNING_KEY" =~ ^[a-f0-9]{64}$ || ! "$FINANCIAL_WRITE_DB_PASSWORD" =~ ^[a-f0-9]{64}$ || ! "$FINANCIAL_READ_DB_PASSWORD" =~ ^[a-f0-9]{64}$ || ! "$FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD" =~ ^[a-f0-9]{64}$ || ! "$FINANCIAL_READ_MIGRATOR_DB_PASSWORD" =~ ^[a-f0-9]{64}$ ]]; then
  echo "Segredo do sandbox ausente ou inválido em $ENV_FILE." >&2
  exit 1
fi

temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT
umask 077
printf "CREATE ROLE financial_write_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '%s';\nCREATE ROLE financial_write_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '%s';\nCREATE DATABASE financial_write_monitoring OWNER financial_write_migrator;\nGRANT CONNECT ON DATABASE financial_write_monitoring TO financial_write_app;\n" \
  "$FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD" "$FINANCIAL_WRITE_DB_PASSWORD" > "$temp_dir/write.sql"
printf "CREATE ROLE financial_read_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '%s';\nCREATE ROLE financial_read_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '%s';\nCREATE DATABASE financial_read_monitoring OWNER financial_read_migrator;\nGRANT CONNECT ON DATABASE financial_read_monitoring TO financial_read_app;\n" \
  "$FINANCIAL_READ_MIGRATOR_DB_PASSWORD" "$FINANCIAL_READ_DB_PASSWORD" > "$temp_dir/read.sql"

cat > "$temp_dir/write_grants.sql" <<'SQL'
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO financial_write_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_write_migrator IN SCHEMA public
    REVOKE ALL PRIVILEGES ON TABLES FROM financial_write_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_write_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE ON TABLES TO financial_write_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_write_migrator IN SCHEMA public
    REVOKE ALL PRIVILEGES ON SEQUENCES FROM financial_write_app;
SQL
cat > "$temp_dir/read_grants.sql" <<'SQL'
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO financial_read_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_read_migrator IN SCHEMA public
    REVOKE ALL PRIVILEGES ON TABLES FROM financial_read_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_read_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE ON TABLES TO financial_read_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_read_migrator IN SCHEMA public
    REVOKE ALL PRIVILEGES ON SEQUENCES FROM financial_read_app;
SQL

docker exec -i "$WRITE_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d postgres < "$temp_dir/write.sql"
docker exec -i "$READ_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d postgres < "$temp_dir/read.sql"
docker exec -i "$WRITE_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d financial_write_monitoring < "$temp_dir/write_grants.sql"
docker exec -i "$READ_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d financial_read_monitoring < "$temp_dir/read_grants.sql"

write_privileges="$(docker exec "$WRITE_CONTAINER" psql -U financial -d financial_write_monitoring -Atqc \
  "SELECT pg_get_userbyid(datdba) = 'financial_write_migrator' AND NOT has_database_privilege('financial_write_app', current_database(), 'CREATE') AND NOT has_schema_privilege('financial_write_app', 'public', 'CREATE') FROM pg_database WHERE datname = current_database()")"
read_privileges="$(docker exec "$READ_CONTAINER" psql -U financial -d financial_read_monitoring -Atqc \
  "SELECT pg_get_userbyid(datdba) = 'financial_read_migrator' AND NOT has_database_privilege('financial_read_app', current_database(), 'CREATE') AND NOT has_schema_privilege('financial_read_app', 'public', 'CREATE') FROM pg_database WHERE datname = current_database()")"
if [[ "$write_privileges" != "t" || "$read_privileges" != "t" ]]; then
  echo "A validação de privilégios de runtime/migration falhou." >&2
  exit 1
fi

echo "Bancos dedicados provisionados com papéis separados de runtime e migration. Segredos gravados somente em $ENV_FILE (permissão 600)."
