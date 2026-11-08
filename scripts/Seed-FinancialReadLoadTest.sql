-- Fixture persistente apenas para ensaios da API de leitura.
-- Não representa o ledger autoritativo de escrita.
DO $$
BEGIN
    IF current_database() <> 'financial_read_loadtest' THEN
        RAISE EXCEPTION 'Fixture recusado: banco atual inesperado: %', current_database();
    END IF;
END
$$;

INSERT INTO financial_read_accounts
    (account_id, owner_reference, created_at, balance_cents, last_occurred_at, last_applied_at)
SELECT
    md5('financial-read-loadtest-account-' || account_number)::uuid,
    'loadtest-customer-' || lpad((((account_number - 1) / 250) + 1)::text, 3, '0'),
    now(),
    1000 + (account_number % 10000),
    NULL,
    now()
FROM generate_series(1, 1000) AS series(account_number)
ON CONFLICT (account_id) DO UPDATE SET
    owner_reference = EXCLUDED.owner_reference,
    created_at = EXCLUDED.created_at,
    balance_cents = EXCLUDED.balance_cents,
    last_occurred_at = EXCLUDED.last_occurred_at,
    last_applied_at = EXCLUDED.last_applied_at;

SELECT owner_reference, count(*) AS accounts, sum(balance_cents) AS balance_cents
FROM financial_read_accounts
WHERE owner_reference LIKE 'loadtest-customer-%'
GROUP BY owner_reference
ORDER BY owner_reference;
