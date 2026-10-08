using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using WalletLedger.Api.Domain;
using WalletLedger.Api.Infrastructure.Database;

namespace WalletLedger.Api.Application;

public sealed class LedgerService(IDbConnectionFactory connectionFactory, IConfiguration configuration)
{
    private readonly Guid _systemAccountId = configuration.GetValue<Guid>("SystemAccountId");

    public async Task<AccountResponse> CreateAccountAsync(CreateAccountRequest request, CancellationToken cancellationToken)
    {
        ValidateCurrency(request.Currency);
        var accountId = Guid.NewGuid();
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var accountCommand = new NpgsqlCommand(
            @"INSERT INTO accounts (id, owner_id, type, currency, status, allow_negative) VALUES (@id, @owner, @type, @currency, 'Active', @allow_negative)", connection, transaction))
        {
            accountCommand.Parameters.AddWithValue("id", accountId);
            accountCommand.Parameters.AddWithValue("owner", request.OwnerId);
            accountCommand.Parameters.AddWithValue("type", request.Type.ToString());
            accountCommand.Parameters.AddWithValue("currency", request.Currency.ToUpperInvariant());
            accountCommand.Parameters.AddWithValue("allow_negative", request.Type == AccountType.System);
            await accountCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var balanceCommand = new NpgsqlCommand(
            "INSERT INTO account_balances (account_id) VALUES (@id)", connection, transaction))
        {
            balanceCommand.Parameters.AddWithValue("id", accountId);
            await balanceCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AccountResponse(accountId, request.OwnerId, request.Type, request.Currency.ToUpperInvariant(), AccountStatus.Active);
    }

    public async Task<BalanceResponse> GetBalanceAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            @"SELECT a.currency, b.balance, b.held, b.version FROM accounts a JOIN account_balances b ON b.account_id = a.id WHERE a.id = @id", connection);
        command.Parameters.AddWithValue("id", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new NotFoundException($"Account {accountId} was not found.");

        var balance = reader.GetInt64(1);
        var held = reader.GetInt64(2);
        return new BalanceResponse(accountId, reader.GetString(0).Trim(), balance - held, held, balance, reader.GetInt64(3));
    }

    public Task<TransactionResponse> TopUpAsync(string idempotencyKey, TopUpRequest request, CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(idempotencyKey, request, TransactionType.Topup, async (connection, transaction) =>
        {
            if (_systemAccountId == Guid.Empty)
                throw new ValidationException("SystemAccountId must be configured for top-ups.");
            return await PostBalancedTransactionAsync(connection, transaction, request.AccountId, _systemAccountId, request.Amount, request.Currency, request.Reference, TransactionType.Topup, cancellationToken);
        }, cancellationToken);

    public Task<TransactionResponse> TransferAsync(string idempotencyKey, TransferRequest request, CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(idempotencyKey, request, TransactionType.Transfer, async (connection, transaction) =>
        {
            return await PostBalancedTransactionAsync(connection, transaction, request.FromAccountId, request.ToAccountId, request.Amount, request.Currency, request.Reference, TransactionType.Transfer, cancellationToken);
        }, cancellationToken);

    public async Task<IReadOnlyList<LedgerEntry>> GetEntriesAsync(Guid accountId, long? beforeId, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ValidationException("limit must be between 1 and 100.");
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            @"SELECT id, transaction_id, account_id, direction, amount, balance_after, created_at FROM ledger_entries WHERE account_id = @account_id AND (@before_id IS NULL OR id < @before_id) ORDER BY id DESC LIMIT @limit", connection);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("before_id", (object?)beforeId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<LedgerEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new LedgerEntry(reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2),
                Enum.Parse<EntryDirection>(reader.GetString(3)), reader.GetInt64(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return entries;
    }

    private async Task<TransactionResponse> ExecuteIdempotentAsync<TRequest>(
        string key,
        TRequest request,
        TransactionType type,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<TransactionResponse>> operation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ValidationException("Idempotency-Key is required.");
        var requestHash = ComputeHash(request);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int inserted;
        await using (var insert = new NpgsqlCommand(
            @"INSERT INTO idempotency_records (key, request_hash, locked_at) VALUES (@key, @hash, now()) ON CONFLICT (key) DO NOTHING", connection, transaction))
        {
            insert.Parameters.AddWithValue("key", key);
            insert.Parameters.AddWithValue("hash", requestHash);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var existing = new NpgsqlCommand(
            "SELECT request_hash, response_status, response_body, locked_at FROM idempotency_records WHERE key = @key FOR UPDATE", connection, transaction);
        existing.Parameters.AddWithValue("key", key);
        await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Idempotency record disappeared.");
        var savedHash = reader.GetString(0);
        var responseBody = reader.IsDBNull(2) ? null : reader.GetString(2);
        var lockedAt = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3);
        await reader.CloseAsync();

        if (savedHash != requestHash) throw new ConflictException("The idempotency key was already used with a different request.");
        if (responseBody is not null) return JsonSerializer.Deserialize<TransactionResponse>(responseBody)!;
        if (inserted == 0 && lockedAt is not null) throw new ConflictException("The request is already being processed.");

        var result = await operation(connection, transaction);
        await using var update = new NpgsqlCommand(
            "UPDATE idempotency_records SET response_status = 200, response_body = @body, locked_at = NULL WHERE key = @key", connection, transaction);
        update.Parameters.AddWithValue("key", key);
        update.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(result) });
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<TransactionResponse> PostBalancedTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid debitAccountId,
        Guid creditAccountId,
        long amount,
        string currency,
        string? reference,
        TransactionType type,
        CancellationToken cancellationToken)
    {
        if (debitAccountId == creditAccountId) throw new ValidationException("Source and destination accounts must differ.");
        if (amount <= 0) throw new ValidationException("Amount must be greater than zero.");
        ValidateCurrency(currency);

        var firstId = debitAccountId.CompareTo(creditAccountId) < 0 ? debitAccountId : creditAccountId;
        var secondId = firstId == debitAccountId ? creditAccountId : debitAccountId;
        await using var lockCommand = new NpgsqlCommand(
            @"SELECT account_id, balance, held, currency, status, allow_negative FROM account_balances JOIN accounts USING (account_id) WHERE account_id IN (@first, @second) ORDER BY account_id FOR UPDATE", connection, transaction);
        lockCommand.Parameters.AddWithValue("first", firstId);
        lockCommand.Parameters.AddWithValue("second", secondId);
        await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
        var balances = new Dictionary<Guid, (long Balance, long Held, string Currency, string Status, bool AllowNegative)>();
        while (await reader.ReadAsync(cancellationToken))
            balances[reader.GetGuid(0)] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3).Trim(), reader.GetString(4), reader.GetBoolean(5));
        await reader.CloseAsync();

        if (!balances.TryGetValue(debitAccountId, out var debit) || !balances.TryGetValue(creditAccountId, out var credit))
            throw new NotFoundException("One or more accounts were not found.");
        if (debit.Status != "Active" || credit.Status != "Active") throw new ConflictException("Both accounts must be active.");
        if (!string.Equals(debit.Currency, currency, StringComparison.OrdinalIgnoreCase) || !string.Equals(credit.Currency, currency, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("All accounts and the request must use the same currency.");
        if (!debit.AllowNegative && debit.Balance - debit.Held < amount) throw new InsufficientFundsException();

        var transactionId = Guid.NewGuid();
        var debitAfter = debit.Balance - amount;
        var creditAfter = credit.Balance + amount;
        await using (var command = new NpgsqlCommand(
            "INSERT INTO transactions (id, type, reference) VALUES (@id, @type, @reference)", connection, transaction))
        {
            command.Parameters.AddWithValue("id", transactionId);
            command.Parameters.AddWithValue("type", type.ToString());
            command.Parameters.AddWithValue("reference", (object?)reference ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new NpgsqlCommand(
            @"INSERT INTO ledger_entries (transaction_id, account_id, direction, amount, balance_after) VALUES (@transaction_id, @debit_account, 'Debit', @amount, @debit_after), (@transaction_id, @credit_account, 'Credit', @amount, @credit_after)", connection, transaction))
        {
            command.Parameters.AddWithValue("transaction_id", transactionId);
            command.Parameters.AddWithValue("debit_account", debitAccountId);
            command.Parameters.AddWithValue("credit_account", creditAccountId);
            command.Parameters.AddWithValue("amount", amount);
            command.Parameters.AddWithValue("debit_after", debitAfter);
            command.Parameters.AddWithValue("credit_after", creditAfter);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new NpgsqlCommand(
            @"UPDATE account_balances SET balance = CASE WHEN account_id = @debit_account THEN balance - @amount ELSE balance END, version = version + 1 WHERE account_id = @debit_account; UPDATE account_balances SET balance = CASE WHEN account_id = @credit_account THEN balance + @amount ELSE balance END, version = version + 1 WHERE account_id = @credit_account; INSERT INTO outbox_messages (id, type, payload) VALUES (@outbox_id, @event_type, @payload)", connection, transaction))
        {
            command.Parameters.AddWithValue("debit_account", debitAccountId);
            command.Parameters.AddWithValue("credit_account", creditAccountId);
            command.Parameters.AddWithValue("amount", amount);
            command.Parameters.AddWithValue("outbox_id", Guid.NewGuid());
            command.Parameters.AddWithValue("event_type", $"{type}Posted");
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { transactionId, debitAccountId, creditAccountId, amount, currency }) });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return new TransactionResponse(transactionId, "Posted", amount, currency.ToUpperInvariant());
    }

    private static string ComputeHash<T>(T request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
    private static void ValidateCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3) throw new ValidationException("Currency must be a 3-letter ISO code.");
    }
}
