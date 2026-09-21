using System.IO;
using System.Text;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Vault;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class AccountEnrollmentServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryWinCredStore _winCredStore;
    private readonly FakeDpapiProvider _fakeDpapi;
    private readonly SessionVault _sessionVault;
    private readonly InMemoryAccountStore _accountStore;
    private readonly MockAG2Adapter _mockAdapter;

    public AccountEnrollmentServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ag2_enroll_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _winCredStore = new InMemoryWinCredStore();
        _fakeDpapi = new FakeDpapiProvider();
        _sessionVault = new SessionVault(_tempDir, _fakeDpapi);
        _accountStore = new InMemoryAccountStore();
        _mockAdapter = new MockAG2Adapter();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EnrollCurrentAccount_WhenDaemonOffline_ThrowsAccountEnrollmentException()
    {
        _mockAdapter.CurrentAccount = null; // Offline / no account
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);

        var ex = await Assert.ThrowsAsync<AccountEnrollmentException>(() =>
            service.EnrollCurrentAccountAsync());

        Assert.Contains("offline", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnrollCurrentAccount_WhenWinCredMissing_ThrowsAccountEnrollmentException()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("dev@example.com", "Developer");
        _winCredStore.Clear(); // No credential in store
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);

        var ex = await Assert.ThrowsAsync<AccountEnrollmentException>(() =>
            service.EnrollCurrentAccountAsync());

        Assert.Contains("No Windows Credential found", ex.Message);
    }

    [Fact]
    public async Task EnrollCurrentAccount_WhenBlobMalformed_ThrowsAccountEnrollmentException()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("dev@example.com", "Developer");
        _winCredStore.Seed("gemini:antigravity", "antigravity", Encoding.UTF8.GetBytes("not-valid-json"));
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);

        var ex = await Assert.ThrowsAsync<AccountEnrollmentException>(() =>
            service.EnrollCurrentAccountAsync());

        Assert.Contains("Failed to parse Windows Credential JSON", ex.Message);
    }

    [Fact]
    public async Task EnrollCurrentAccount_WithValidAccountAndWinCred_EnrollsNewAccountSuccessfully()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("dev@example.com", "Developer");
        byte[] validBlob = Encoding.UTF8.GetBytes("{\"token\":\"synthetic-token-12345\",\"auth_method\":\"oauth\"}");
        _winCredStore.Seed("gemini:antigravity", "antigravity", validBlob);

        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);

        var result = await service.EnrollCurrentAccountAsync(new EnrollmentOptions(
            Name: "Custom Display Name",
            Priority: 1,
            Notes: "Enrolled test account"
        ));

        Assert.True(result.Success);
        Assert.True(result.IsNew);
        Assert.Equal("dev@example.com", result.Account.Email);
        Assert.Equal("Custom Display Name", result.Account.Name);
        Assert.Equal(AccountValidationStatus.Valid, result.Account.ValidationStatus);
        Assert.True(result.Account.HasVaultedSession);

        // Verify saved in account store
        var stored = await _accountStore.GetAccountByEmailAsync("dev@example.com");
        Assert.NotNull(stored);
        Assert.Equal(result.Account.Id, stored.Id);

        // Verify active account set
        Assert.Equal(result.Account.Id, await _accountStore.GetActiveAccountIdAsync());

        // Verify saved in session vault
        Assert.True(await _sessionVault.HasSessionAsync(result.Account.Id));
        var retrievedBlob = await _sessionVault.GetSessionAsync(result.Account.Id);
        Assert.Equal(validBlob, retrievedBlob);
    }

    [Fact]
    public async Task EnrollCurrentAccount_WhenDuplicateEmail_UpdatesExistingRecordWithoutDuplication()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("duplicate@example.com", "Original Name");
        byte[] blob1 = Encoding.UTF8.GetBytes("{\"token\":\"token-1\",\"auth_method\":\"oauth\"}");
        _winCredStore.Seed("gemini:antigravity", "antigravity", blob1);

        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);

        // First enrollment
        var firstResult = await service.EnrollCurrentAccountAsync(new EnrollmentOptions(Priority: 1));
        Assert.True(firstResult.IsNew);
        string originalId = firstResult.Account.Id;

        // Second enrollment with updated options and new credential blob
        byte[] blob2 = Encoding.UTF8.GetBytes("{\"token\":\"token-2-refreshed\",\"auth_method\":\"oauth\"}");
        _winCredStore.Seed("gemini:antigravity", "antigravity", blob2);

        var secondResult = await service.EnrollCurrentAccountAsync(new EnrollmentOptions(
            Name: "Updated Name",
            Priority: 3,
            Notes: "Updated notes"
        ));

        Assert.True(secondResult.Success);
        Assert.False(secondResult.IsNew); // Not a new account!
        Assert.Equal(originalId, secondResult.Account.Id); // ID is preserved
        Assert.Equal("Updated Name", secondResult.Account.Name);
        Assert.Equal(3, secondResult.Account.Priority);

        // Confirm store only has 1 account
        var allAccounts = await _accountStore.ListAccountsAsync();
        Assert.Single(allAccounts);

        // Confirm vault was updated with new blob
        var retrieved = await _sessionVault.GetSessionAsync(originalId);
        Assert.Equal(blob2, retrieved);
    }
}
