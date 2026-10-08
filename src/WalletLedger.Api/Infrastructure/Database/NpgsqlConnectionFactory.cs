using Npgsql;

namespace WalletLedger.Api.Infrastructure.Database;

public interface IDbConnectionFactory
{
    NpgsqlConnection Create();
}

public sealed class NpgsqlConnectionFactory(IConfiguration configuration) : IDbConnectionFactory
{
    // Keep connection creation behind an interface so application code is easy to test and replace.
    private readonly string _connectionString = configuration.GetConnectionString("WalletLedger")
        ?? throw new InvalidOperationException("ConnectionStrings:WalletLedger is required.");

    public NpgsqlConnection Create() => new(_connectionString);
}
