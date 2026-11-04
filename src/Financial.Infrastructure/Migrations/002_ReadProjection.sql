CREATE TABLE IF NOT EXISTS financial_read_accounts (
    account_id uuid PRIMARY KEY,
    owner_reference varchar(100) NOT NULL,
    created_at timestamptz NOT NULL,
    balance_cents bigint NOT NULL DEFAULT 0 CHECK (balance_cents >= 0),
    last_occurred_at timestamptz NULL,
    last_applied_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_financial_read_accounts_owner
    ON financial_read_accounts (owner_reference);

CREATE TABLE IF NOT EXISTS financial_read_entries (
    entry_id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES financial_read_accounts(account_id),
    entry_type varchar(6) NOT NULL CHECK (entry_type IN ('credit', 'debit')),
    amount_cents bigint NOT NULL CHECK (amount_cents > 0),
    signed_amount_cents bigint NOT NULL,
    idempotency_key varchar(100) NOT NULL,
    description varchar(250) NULL,
    occurred_at timestamptz NOT NULL,
    CONSTRAINT uq_financial_read_entries_idempotency UNIQUE (account_id, idempotency_key),
    CONSTRAINT uq_financial_read_entries_account_time UNIQUE (account_id, occurred_at)
);

CREATE INDEX IF NOT EXISTS ix_financial_read_entries_account_time
    ON financial_read_entries (account_id, occurred_at);

CREATE TABLE IF NOT EXISTS financial_projected_events (
    event_id uuid PRIMARY KEY,
    projected_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS financial_projection_checkpoint (
    checkpoint_id integer PRIMARY KEY CHECK (checkpoint_id = 1),
    last_event_created_at timestamptz NOT NULL,
    last_event_id uuid NOT NULL,
    updated_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS financial_read_access_audit (
    audit_id uuid PRIMARY KEY,
    actor_reference varchar(100) NOT NULL,
    action varchar(80) NOT NULL,
    resource_id uuid NULL,
    occurred_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_financial_read_access_audit_actor_time
    ON financial_read_access_audit (actor_reference, occurred_at);

CREATE TABLE IF NOT EXISTS financial_projection_status (
    status_id integer PRIMARY KEY CHECK (status_id = 1),
    worker_heartbeat_at timestamptz NOT NULL,
    pending_count bigint NOT NULL,
    oldest_pending_at timestamptz NULL,
    dead_letter_count bigint NOT NULL,
    updated_at timestamptz NOT NULL
);
