namespace WalletLedger.Api.Application;

public abstract class LedgerException(string message) : Exception(message);
public sealed class ValidationException(string message) : LedgerException(message);
public sealed class NotFoundException(string message) : LedgerException(message);
public sealed class ConflictException(string message) : LedgerException(message);
public sealed class InsufficientFundsException() : LedgerException("Insufficient available funds.");
