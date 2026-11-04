ALTER TABLE financial_outbox
    ADD COLUMN IF NOT EXISTS dead_lettered_at timestamptz NULL;

CREATE INDEX IF NOT EXISTS ix_financial_outbox_dead_letters
    ON financial_outbox (dead_lettered_at) WHERE dead_lettered_at IS NOT NULL;

CREATE TABLE IF NOT EXISTS financial_audit_events (
    audit_id uuid PRIMARY KEY,
    actor_reference varchar(100) NOT NULL,
    action varchar(80) NOT NULL,
    account_id uuid NOT NULL,
    transaction_id uuid NULL,
    occurred_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_financial_audit_events_account_time
    ON financial_audit_events (account_id, occurred_at);

CREATE INDEX IF NOT EXISTS ix_financial_audit_events_actor_time
    ON financial_audit_events (actor_reference, occurred_at);
