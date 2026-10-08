# Wallet & Ledger Service

Backend service for wallet balances and double-entry ledger operations. The project is designed as a portfolio-grade demonstration of money movement under retries, concurrency and partial failures.

## What this MVP proves

- Money is stored as `long` minor units, never floating point.
- Transfers write one debit and one credit in the same PostgreSQL transaction.
- Two account rows are locked in deterministic UUID order to reduce deadlocks.
- `Idempotency-Key` stores the original response and rejects a reused key with a different payload.
- Ledger entries are append-only and database triggers reject unbalanced transactions and mutations.
- An outbox message is committed with the ledger transaction.
- Cursor-based ledger entry reads are available through `beforeId`.

## Stack

- .NET 10 / ASP.NET Core Minimal API
- PostgreSQL 17
- Npgsql
- Scalar OpenAPI UI in Development
- Docker Compose

## Project layout

```text
.
├── src/WalletLedger.Api
│   ├── Application       # Transaction orchestration and business errors
│   ├── Domain            # Money movement contracts and models
│   └── Infrastructure     # PostgreSQL connection and schema
├── tests                 # Integration and concurrency tests to expand
├── http                  # REST Client demo requests
├── DESIGN.md             # Architecture decisions and trade-offs
├── docker-compose.yml
└── WalletLedger.slnx
```

## Run locally

Prerequisites: Docker Desktop and .NET 10 SDK.

```powershell
docker compose up -d
 dotnet run --project src/WalletLedger.Api --environment Development
```

Open `http://localhost:5000/scalar` for the API explorer. The default connection string is local-only and must be replaced through environment variables outside development.

```powershell
$env:ConnectionStrings__WalletLedger = "Host=...;Database=...;Username=...;Password=..."
```

The Compose schema creates a VND system account with id `00000000-0000-0000-0000-000000000001`, used as the source of top-ups in the demo flow.

## Try the API

Use VS Code REST Client or JetBrains HTTP Client. Select the `dev` environment from `http/http-client.env.json`, then run requests in `http/01-accounts-and-transfers.http` in order.

The important demonstration is request 5: sending the same transfer and the same idempotency key again returns the original transaction instead of creating a second debit.

## Verification roadmap

The next tests should run against a real PostgreSQL container, not EF InMemory:

1. 1,000 concurrent transfers across 10 wallets and a conservation-of-money assertion.
2. 20 concurrent retries with one idempotency key and exactly one transaction.
3. Rejection of an unbalanced transaction and every ledger update/delete.
4. Outbox publisher retry and idempotent consumer behavior.
5. k6 load tests with p95/p99 latency and a hot-account comparison.

## GitHub presentation

Before publishing, replace the default repository metadata, add screenshots of Scalar and a short load-test result, then push:

```powershell
git init
git add .
git commit -m "Build wallet ledger MVP"
git branch -M main
git remote add origin https://github.com/quanhoang08/Wallet-Ledger-Service-.git
git push -u origin main
```

Never commit real credentials. Keep local secrets in `.env` or user secrets.
