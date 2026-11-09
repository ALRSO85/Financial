#!/usr/bin/env bash
set -euo pipefail

ENV_FILE="${1:-.env}"
WRITE_CONTAINER="${FINANCIAL_WRITE_CONTAINER:-financial-write-db}"
READ_CONTAINER="${FINANCIAL_READ_CONTAINER:-financial-read-db}"

if ! command -v docker >/dev/null 2>&1 || ! command -v openssl >/dev/null 2>&1; then
  echo "Docker e OpenSSL são necessários." >&2
  exit 1
fi
if [[ ! -f "$ENV_FILE" ]]; then
  echo "Arquivo de configuração não encontrado: $ENV_FILE" >&2
  exit 1
fi
chmod 600 "$ENV_FILE"

read_env_value() {
  sed -n "s/^${1}=//p" "$ENV_FILE" | head -n 1 | tr -d '\r'
}

ensure_secret() {
  local key="$1"
  local current
  local occurrences
  local generated
  current="$(read_env_value "$key")"
  occurrences="$(grep -c "^${key}=" "$ENV_FILE" || true)"
  if (( occurrences > 1 )); then
    echo "A configuração $key aparece mais de uma vez em $ENV_FILE." >&2
    exit 1
  fi
  if [[ -z "$current" || "$current" == replace-with-* ]]; then
    generated="$(openssl rand -hex 32)"
    if (( occurrences == 0 )); then
      printf '%s=%s\n' "$key" "$generated" >> "$ENV_FILE"
    else
      sed -i "s/^${key}=.*/${key}=${generated}/" "$ENV_FILE"
    fi
  fi
}

write_state="$(docker exec "$WRITE_CONTAINER" psql -U financial -d postgres -Atqc \
  "SELECT (SELECT count(*) FROM pg_roles WHERE rolname='financial_write_app') || '|' || (SELECT count(*) FROM pg_database WHERE datname='financial_write_monitoring')")"
read_state="$(docker exec "$READ_CONTAINER" psql -U financial -d postgres -Atqc \
  "SELECT (SELECT count(*) FROM pg_roles WHERE rolname='financial_read_app') || '|' || (SELECT count(*) FROM pg_database WHERE datname='financial_read_monitoring')")"
if [[ "$write_state" != "1|1" || "$read_state" != "1|1" ]]; then
  echo "O sandbox existente não corresponde ao par de bancos esperado; nenhuma alteração foi feita." >&2
  exit 2
fi

ensure_secret FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD
ensure_secret FINANCIAL_READ_MIGRATOR_DB_PASSWORD
FINANCIAL_WRITE_DB_PASSWORD="$(read_env_value FINANCIAL_WRITE_DB_PASSWORD)"
FINANCIAL_READ_DB_PASSWORD="$(read_env_value FINANCIAL_READ_DB_PASSWORD)"
FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD="$(read_env_value FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD)"
FINANCIAL_READ_MIGRATOR_DB_PASSWORD="$(read_env_value FINANCIAL_READ_MIGRATOR_DB_PASSWORD)"
for key in FINANCIAL_WRITE_DB_PASSWORD FINANCIAL_READ_DB_PASSWORD FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD FINANCIAL_READ_MIGRATOR_DB_PASSWORD; do
  occurrences="$(grep -c "^${key}=" "$ENV_FILE" || true)"
  if [[ "$occurrences" != "1" ]]; then
    echo "A configuração $key deve aparecer exatamente uma vez em $ENV_FILE." >&2
    exit 1
  fi
done
if [[ ! "$FINANCIAL_WRITE_DB_PASSWORD" =~ ^[a-f0-9]{64}$ || ! "$FINANCIAL_READ_DB_PASSWORD" =~ ^[a-f0-9]{64}$ || ! "$FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD" =~ ^[a-f0-9]{64}$ || ! "$FINANCIAL_READ_MIGRATOR_DB_PASSWORD" =~ ^[a-f0-9]{64}$ ]]; then
  echo "Credenciais de runtime/migration ausentes ou inválidas em $ENV_FILE." >&2
  exit 1
fi

temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT
umask 077

write_migrator_exists="$(docker exec "$WRITE_CONTAINER" psql -U financial -d postgres -Atqc \
  "SELECT count(*) FROM pg_roles WHERE rolname='financial_write_migrator'")"
read_migrator_exists="$(docker exec "$READ_CONTAINER" psql -U financial -d postgres -Atqc \
  "SELECT count(*) FROM pg_roles WHERE rolname='financial_read_migrator'")"
if [[ "$write_migrator_exists" == "0" ]]; then
  printf "CREATE ROLE financial_write_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '%s';\n" "$FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD" > "$temp_dir/write_role.sql"
else
  printf "ALTER ROLE financial_write_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '%s';\n" "$FINANCIAL_WRITE_MIGRATOR_DB_PASSWORD" > "$temp_dir/write_role.sql"
fi
if [[ "$read_migrator_exists" == "0" ]]; then
  printf "CREATE ROLE financial_read_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '%s';\n" "$FINANCIAL_READ_MIGRATOR_DB_PASSWORD" > "$temp_dir/read_role.sql"
else
  printf "ALTER ROLE financial_read_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '%s';\n" "$FINANCIAL_READ_MIGRATOR_DB_PASSWORD" > "$temp_dir/read_role.sql"
fi

docker exec -i "$WRITE_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d postgres < "$temp_dir/write_role.sql"
docker exec -i "$READ_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d postgres < "$temp_dir/read_role.sql"
docker exec "$WRITE_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d postgres -c \
  "ALTER DATABASE financial_write_monitoring OWNER TO financial_write_migrator"
docker exec "$READ_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d postgres -c \
  "ALTER DATABASE financial_read_monitoring OWNER TO financial_read_migrator"

cat > "$temp_dir/write_grants.sql" <<'SQL'
REASSIGN OWNED BY financial_write_app TO financial_write_migrator;
ALTER ROLE financial_write_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
REVOKE ALL ON DATABASE financial_write_monitoring FROM financial_write_app;
GRANT CONNECT ON DATABASE financial_write_monitoring TO financial_write_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC, financial_write_app;
GRANT USAGE ON SCHEMA public TO financial_write_app;
REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM financial_write_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO financial_write_app;
REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM financial_write_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_write_migrator IN SCHEMA public
    REVOKE ALL PRIVILEGES ON TABLES FROM financial_write_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_write_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE ON TABLES TO financial_write_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_write_migrator IN SCHEMA public
    REVOKE ALL PRIVILEGES ON SEQUENCES FROM financial_write_app;
SQL
cat > "$temp_dir/read_grants.sql" <<'SQL'
REASSIGN OWNED BY financial_read_app TO financial_read_migrator;
ALTER ROLE financial_read_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
REVOKE ALL ON DATABASE financial_read_monitoring FROM financial_read_app;
GRANT CONNECT ON DATABASE financial_read_monitoring TO financial_read_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC, financial_read_app;
GRANT USAGE ON SCHEMA public TO financial_read_app;
REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM financial_read_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO financial_read_app;
REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM financial_read_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_read_migrator IN SCHEMA public
    REVOKE ALL PRIVILEGES ON TABLES FROM financial_read_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_read_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE ON TABLES TO financial_read_app;
ALTER DEFAULT PRIVILEGES FOR ROLE financial_read_migrator IN SCHEMA public
    REVOKE ALL PRIVILEGES ON SEQUENCES FROM financial_read_app;
SQL

docker exec -i "$WRITE_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d financial_write_monitoring < "$temp_dir/write_grants.sql"
docker exec -i "$READ_CONTAINER" psql -v ON_ERROR_STOP=1 -U financial -d financial_read_monitoring < "$temp_dir/read_grants.sql"

write_privileges="$(docker exec "$WRITE_CONTAINER" psql -U financial -d financial_write_monitoring -Atqc \
  "SELECT pg_get_userbyid(datdba) = 'financial_write_migrator' AND NOT has_database_privilege('financial_write_app', current_database(), 'CREATE') AND NOT has_schema_privilege('financial_write_app', 'public', 'CREATE') AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f') AND has_table_privilege('financial_write_app', c.oid, 'DELETE')) AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relowner='financial_write_app'::regrole) FROM pg_database WHERE datname=current_database()")"
read_privileges="$(docker exec "$READ_CONTAINER" psql -U financial -d financial_read_monitoring -Atqc \
  "SELECT pg_get_userbyid(datdba) = 'financial_read_migrator' AND NOT has_database_privilege('financial_read_app', current_database(), 'CREATE') AND NOT has_schema_privilege('financial_read_app', 'public', 'CREATE') AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f') AND has_table_privilege('financial_read_app', c.oid, 'DELETE')) AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relowner='financial_read_app'::regrole) FROM pg_database WHERE datname=current_database()")"
if [[ "$write_privileges" != "t" || "$read_privileges" != "t" ]]; then
  echo "A validação de privilégios de runtime/migration falhou; mantenha as APIs em execução e corrija a transição antes de reiniciá-las." >&2
  exit 1
fi

echo "Papéis migrator separados e verificados. Credenciais mantidas em $ENV_FILE (permissão 600); senhas dos papéis app não foram alteradas."
