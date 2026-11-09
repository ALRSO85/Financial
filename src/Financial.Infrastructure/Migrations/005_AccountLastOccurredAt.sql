ALTER TABLE financial_accounts
    ADD COLUMN IF NOT EXISTS last_occurred_at timestamptz NULL;

UPDATE financial_accounts AS account
SET last_occurred_at = COALESCE(
    (SELECT MAX(entry.occurred_at)
     FROM financial_entries AS entry
     WHERE entry.account_id = account.id),
    account.created_at)
WHERE last_occurred_at IS NULL;

ALTER TABLE financial_accounts
    ALTER COLUMN last_occurred_at SET NOT NULL;
