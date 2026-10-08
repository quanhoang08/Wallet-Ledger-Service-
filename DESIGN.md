# Design Notes

## Source of truth

PostgreSQL is the source of truth. `account_balances` is a locked, denormalized read model; `ledger_entries` is the audit trail. A balance can be rebuilt and checked from the entries during reconciliation.

## Why `long`

Amounts are integer minor units: VND uses đồng and USD would use cents. Integer arithmetic avoids rounding behavior and makes conservation assertions exact.

## Transfer transaction

1. Insert or lock the idempotency record.
2. Lock both balance rows in ascending UUID order.
3. Validate account status, currency and available funds.
4. Insert the transaction and its debit/credit entries.
5. Update both balances and append an outbox message.
6. Store the response body and commit once.

The deterministic lock order is important when A sends to B while B sends to A. Locking the sender first would create a deadlock cycle.

## Idempotency

The request payload is hashed with SHA-256. A repeated key with the same hash returns the stored response. A repeated key with a different hash returns `409 Conflict`. The unique database key is the final protection against races.

## Database invariants

- `ledger_entries` has a trigger rejecting `UPDATE` and `DELETE`.
- A deferred constraint trigger checks that every transaction has equal debit and credit totals at commit.
- Amounts must be positive.
- A normal account cannot spend more than `balance - held`.

The deferred check is deliberately in the database so a future worker or migration cannot bypass the core money invariant by accident.

## Deliberate MVP boundaries

Authentication, holds, reversals, withdrawals, webhook HMAC verification, reconciliation jobs and an outbox publisher are not hidden behind fake implementations. They are the next vertical slices. Keeping them out of the first commit makes the concurrency and ledger guarantees reviewable.

## Production follow-ups

- Use a migration tool and separate deployment-time schema changes from startup.
- Add Testcontainers integration tests and FsCheck property tests.
- Add an outbox publisher using `FOR UPDATE SKIP LOCKED` and idempotent consumers.
- Add structured logs, OpenTelemetry traces, metrics and rate limits.
- Use a dedicated database role that cannot mutate `ledger_entries`.
