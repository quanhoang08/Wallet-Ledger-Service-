namespace WalletLedger.Api.Domain;

public enum AccountType { User, Merchant, System }
public enum AccountStatus { Active, Frozen, Closed }
public enum TransactionType { Topup, Transfer, Withdrawal, Refund, Reversal }
public enum EntryDirection { Debit, Credit }

public sealed record Account(
    Guid Id,
    Guid OwnerId,
    AccountType Type,
    string Currency,
    AccountStatus Status,
    bool AllowNegative,
    DateTimeOffset CreatedAt);

public sealed record AccountBalance(Guid AccountId, long Balance, long Held, long Version);
public sealed record LedgerEntry(long Id, Guid TransactionId, Guid AccountId, EntryDirection Direction, long Amount, long BalanceAfter, DateTimeOffset CreatedAt);

public sealed record CreateAccountRequest(Guid OwnerId, AccountType Type, string Currency);
public sealed record TopUpRequest(Guid AccountId, long Amount, string Currency, string? Reference);
public sealed record TransferRequest(Guid FromAccountId, Guid ToAccountId, long Amount, string Currency, string? Reference);
public sealed record AccountResponse(Guid Id, Guid OwnerId, AccountType Type, string Currency, AccountStatus Status);
public sealed record BalanceResponse(Guid AccountId, string Currency, long Available, long Held, long Balance, long Version);
public sealed record TransactionResponse(Guid TransactionId, string Status, long Amount, string Currency);
