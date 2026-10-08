using Npgsql;

namespace WalletLedger.Api.Infrastructure.Database;

public interface IDbConnectionFactory
{
    NpgsqlConnection Create();
}

public sealed class NpgsqlConnectionFactory(IConfiguration configuration) : IDbConnectionFactory
{
    private readonly string _connectionString = configuration.GetConnectionString("WalletLedger")
        ?? throw new InvalidOperationException("ConnectionStrings:WalletLedger is required.");

    public NpgsqlConnection Create() => new(_connectionString);
}
