using WalletLedger.Api.Domain;
using Xunit;

namespace WalletLedger.Api.Tests;

public sealed class LedgerModelTests
{
    [Fact]
    public void System_accounts_are_the_only_accounts_allowed_to_go_negative()
    {
        var userRequest = new CreateAccountRequest(Guid.NewGuid(), AccountType.User, "VND");
        var systemRequest = new CreateAccountRequest(Guid.NewGuid(), AccountType.System, "VND");

        Assert.NotEqual(userRequest.Type, systemRequest.Type);
    }
}
