CREATE TABLE IF NOT EXISTS financial_accounts (
    id uuid PRIMARY KEY,
    owner_reference varchar(100) NOT NULL,
    created_at timestamptz NOT NULL,
    balance_cents bigint NOT NULL DEFAULT 0 CHECK (balance_cents >= 0)
);

CREATE TABLE IF NOT EXISTS financial_entries (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES financial_accounts(id),
    entry_type varchar(6) NOT NULL CHECK (entry_type IN ('credit', 'debit')),
    amount_cents bigint NOT NULL CHECK (amount_cents > 0),
    signed_amount_cents bigint NOT NULL,
    idempotency_key varchar(100) NOT NULL,
    description varchar(250) NULL,
    occurred_at timestamptz NOT NULL,
    CONSTRAINT ck_financial_entries_signed_amount CHECK (
        (entry_type = 'credit' AND signed_amount_cents = amount_cents) OR
        (entry_type = 'debit' AND signed_amount_cents = -amount_cents)
    ),
    CONSTRAINT uq_financial_entries_idempotency UNIQUE (account_id, idempotency_key),
    CONSTRAINT uq_financial_entries_account_time UNIQUE (account_id, occurred_at)
);

CREATE INDEX IF NOT EXISTS ix_financial_entries_account_time
    ON financial_entries (account_id, occurred_at);

CREATE TABLE IF NOT EXISTS financial_outbox (
    event_id uuid PRIMARY KEY,
    event_type varchar(80) NOT NULL,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    processed_at timestamptz NULL,
    attempts integer NOT NULL DEFAULT 0,
    last_error varchar(1000) NULL
);

CREATE INDEX IF NOT EXISTS ix_financial_outbox_pending
    ON financial_outbox (created_at, event_id) WHERE processed_at IS NULL;

CREATE TABLE IF NOT EXISTS financial_schema_migrations (
    version varchar(100) PRIMARY KEY,
    applied_at timestamptz NOT NULL
);
