CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE IF NOT EXISTS accounts (
    id uuid PRIMARY KEY,
    owner_id uuid NOT NULL,
    type text NOT NULL CHECK (type IN ('User', 'Merchant', 'System')),
    currency char(3) NOT NULL,
    status text NOT NULL CHECK (status IN ('Active', 'Frozen', 'Closed')),
    allow_negative boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS account_balances (
    account_id uuid PRIMARY KEY REFERENCES accounts(id),
    balance bigint NOT NULL DEFAULT 0,
    held bigint NOT NULL DEFAULT 0,
    version bigint NOT NULL DEFAULT 0,
    CHECK (held >= 0)
);

CREATE TABLE IF NOT EXISTS transactions (
    id uuid PRIMARY KEY,
    type text NOT NULL,
    status text NOT NULL DEFAULT 'Posted',
    idempotency_key text UNIQUE,
    reference text,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    reversal_of uuid NULL REFERENCES transactions(id),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS ledger_entries (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    transaction_id uuid NOT NULL REFERENCES transactions(id),
    account_id uuid NOT NULL REFERENCES accounts(id),
    direction text NOT NULL CHECK (direction IN ('Debit', 'Credit')),
    amount bigint NOT NULL CHECK (amount > 0),
    balance_after bigint NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS idempotency_records (
    key text PRIMARY KEY,
    request_hash text NOT NULL,
    response_status int NOT NULL DEFAULT 202,
    response_body jsonb,
    locked_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS outbox_messages (
    id uuid PRIMARY KEY,
    type text NOT NULL,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    processed_at timestamptz NULL
);

CREATE INDEX IF NOT EXISTS ix_ledger_entries_account_cursor
    ON ledger_entries (account_id, id DESC);
CREATE INDEX IF NOT EXISTS ix_outbox_unprocessed
    ON outbox_messages (created_at)
    WHERE processed_at IS NULL;

CREATE OR REPLACE FUNCTION reject_ledger_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'ledger_entries is append-only';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS ledger_entries_immutable ON ledger_entries;
CREATE TRIGGER ledger_entries_immutable
    BEFORE UPDATE OR DELETE ON ledger_entries
    FOR EACH ROW EXECUTE FUNCTION reject_ledger_mutation();

CREATE OR REPLACE FUNCTION validate_transaction_balance() RETURNS trigger AS $$
DECLARE debit_total bigint;
DECLARE credit_total bigint;
BEGIN
    SELECT COALESCE(SUM(amount) FILTER (WHERE direction = 'Debit'), 0),
           COALESCE(SUM(amount) FILTER (WHERE direction = 'Credit'), 0)
      INTO debit_total, credit_total
      FROM ledger_entries
     WHERE transaction_id = NEW.transaction_id;

    IF debit_total <> credit_total THEN
        RAISE EXCEPTION 'transaction % is not balanced', NEW.transaction_id;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS transaction_balance_check ON ledger_entries;
CREATE CONSTRAINT TRIGGER transaction_balance_check
    AFTER INSERT ON ledger_entries
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION validate_transaction_balance();

INSERT INTO accounts (id, owner_id, type, currency, status, allow_negative)
VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001', 'System', 'VND', 'Active', true)
ON CONFLICT (id) DO NOTHING;

INSERT INTO account_balances (account_id)
VALUES ('00000000-0000-0000-0000-000000000001')
ON CONFLICT (account_id) DO NOTHING;
