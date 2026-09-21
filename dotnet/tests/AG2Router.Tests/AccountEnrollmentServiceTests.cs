using System.IO;
using System.Text;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Vault;
using AG2Router.Core.Contracts;
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

    [Fact]
    public async Task EnrollCurrentAccount_WhenVaultSaveFails_DoesNotExposeVaultedMetadata()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("vault-failure@example.com", "Synthetic");
        _winCredStore.Seed("gemini:antigravity", "synthetic", Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var failingVault = new SessionVault(_tempDir, new ThrowingDpapiProvider());
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, failingVault, _accountStore);

        await Assert.ThrowsAsync<AccountEnrollmentException>(() => service.EnrollCurrentAccountAsync());

        Assert.Empty(await _accountStore.ListAccountsAsync());
        Assert.Null(await _accountStore.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task EnrollCurrentAccount_WhenFinalMetadataCommitFails_CompensatesAndRetrySucceeds()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("metadata-failure@example.com", "Synthetic");
        _winCredStore.Seed("gemini:antigravity", "synthetic", Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var failingStore = new FailUpdateAccountStore(_accountStore);
        var failingService = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, failingStore);

        await Assert.ThrowsAsync<AccountEnrollmentException>(() => failingService.EnrollCurrentAccountAsync());
        Assert.Empty(await _accountStore.ListAccountsAsync());
        Assert.Empty(await _sessionVault.ListStoredAccountIdsAsync());
        Assert.Null(await _accountStore.GetActiveAccountIdAsync());

        var retryService = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);
        var retry = await retryService.EnrollCurrentAccountAsync();
        Assert.True(retry.Success);
        Assert.True(retry.Account.HasVaultedSession);
        Assert.True(await _sessionVault.HasSessionAsync(retry.Account.Id));
    }

    [Fact]
    public async Task EnrollCurrentAccount_ConcurrentDuplicateRequestsCreateOneAccount()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("concurrent-enroll@example.com", "Synthetic");
        _winCredStore.Seed("gemini:antigravity", "synthetic", Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);

        var results = await Task.WhenAll(
            service.EnrollCurrentAccountAsync(),
            service.EnrollCurrentAccountAsync());

        Assert.Single(await _accountStore.ListAccountsAsync());
        Assert.Single(results, result => result.IsNew);
        Assert.All(results, result => Assert.True(result.Account.HasVaultedSession));
        Assert.Single(await _sessionVault.ListStoredAccountIdsAsync());
    }

    [Fact]
    public async Task EnrollCurrentAccount_ExistingMetadataFailureRestoresPriorVaultSession()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("existing-failure@example.com", "Synthetic");
        var account = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: "existing-failure@example.com",
            HasVaultedSession: true));
        byte[] prior = Encoding.UTF8.GetBytes("{\"token\":\"prior-synthetic\"}");
        await _sessionVault.SaveSessionAsync(account.Id, prior);
        _winCredStore.Seed("gemini:antigravity", "synthetic", Encoding.UTF8.GetBytes("{\"token\":\"replacement-synthetic\"}"));
        var service = new AccountEnrollmentService(
            _mockAdapter,
            _winCredStore,
            _sessionVault,
            new FailUpdateAccountStore(_accountStore));

        await Assert.ThrowsAsync<AccountEnrollmentException>(() => service.EnrollCurrentAccountAsync());

        Assert.Equal(prior, await _sessionVault.GetSessionAsync(account.Id));
        Assert.True((await _accountStore.GetAccountAsync(account.Id))!.HasVaultedSession);
    }

    [Fact]
    public async Task EnrollmentCompensation_DoesNotOverwriteNewerActiveSelection()
    {
        var previous = await _accountStore.AddAccountAsync(new CreateAccountInput(Email: "previous@example.com"));
        var newer = await _accountStore.AddAccountAsync(new CreateAccountInput(Email: "newer@example.com"));
        await _accountStore.SetActiveAccountIdAsync(previous.Id);
        _mockAdapter.CurrentAccount = new AccountIdentityDto("failing-enrollment@example.com", "Synthetic");
        _winCredStore.Seed("gemini:antigravity", "synthetic", Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var blockingStore = new BlockingFailUpdateAccountStore(_accountStore);
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, blockingStore);

        Task<EnrollmentResult> enrollment = service.EnrollCurrentAccountAsync();
        await blockingStore.UpdateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _accountStore.SetActiveAccountIdAsync(newer.Id);
        blockingStore.ReleaseFailure();

        await Assert.ThrowsAsync<AccountEnrollmentException>(() => enrollment);
        Assert.Equal(newer.Id, await _accountStore.GetActiveAccountIdAsync());
        Assert.Null(await _accountStore.GetAccountByEmailAsync("failing-enrollment@example.com"));
    }

    [Fact]
    public async Task EnrollmentCompensation_DoesNotOverwriteNewerVaultSession()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("vault-race@example.com", "Synthetic");
        var account = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: "vault-race@example.com",
            HasVaultedSession: true));
        byte[] original = Encoding.UTF8.GetBytes("{\"token\":\"original-synthetic\"}");
        byte[] enrollment = Encoding.UTF8.GetBytes("{\"token\":\"enrollment-synthetic\"}");
        byte[] newer = Encoding.UTF8.GetBytes("{\"token\":\"newer-synthetic\"}");
        await _sessionVault.SaveSessionAsync(account.Id, original);
        _winCredStore.Seed("gemini:antigravity", "synthetic", enrollment);
        var blockingStore = new BlockingFailUpdateAccountStore(_accountStore);
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, blockingStore);

        Task<EnrollmentResult> enrollmentTask = service.EnrollCurrentAccountAsync();
        await blockingStore.UpdateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _sessionVault.SaveSessionAsync(account.Id, newer);
        blockingStore.ReleaseFailure();

        await Assert.ThrowsAsync<AccountEnrollmentException>(() => enrollmentTask);
        Assert.Equal(newer, await _sessionVault.GetSessionAsync(account.Id));
    }

    private sealed class ThrowingDpapiProvider : IDpapiProvider
    {
        public Task<byte[]> EncryptAsync(byte[] plaintext, CancellationToken cancellationToken = default) =>
            throw new DpapiException("Injected encryption failure.");

        public Task<byte[]> DecryptAsync(byte[] ciphertext, CancellationToken cancellationToken = default) =>
            throw new DpapiException("Injected decryption failure.");
    }

    private sealed class FailUpdateAccountStore(IAccountStore inner) : IAccountStore
    {
        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
            inner.ListAccountsAsync(cancellationToken);
        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default) =>
            inner.GetAccountAsync(id, cancellationToken);
        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            inner.GetAccountByEmailAsync(email, cancellationToken);
        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default) =>
            inner.AddAccountAsync(input, cancellationToken);
        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default) =>
            throw new IOException("Injected metadata commit failure.");
        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default) =>
            inner.RemoveAccountAsync(id, cancellationToken);
        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default) =>
            inner.GetActiveAccountIdAsync(cancellationToken);
        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default) =>
            inner.SetActiveAccountIdAsync(id, cancellationToken);
        public Task<bool> CompareExchangeActiveAccountIdAsync(
            string? expectedId,
            string? newId,
            CancellationToken cancellationToken = default) =>
            inner.CompareExchangeActiveAccountIdAsync(expectedId, newId, cancellationToken);
    }

    private sealed class BlockingFailUpdateAccountStore(IAccountStore inner) : IAccountStore
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource UpdateEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFailure() => _release.TrySetResult();

        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
            inner.ListAccountsAsync(cancellationToken);
        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default) =>
            inner.GetAccountAsync(id, cancellationToken);
        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            inner.GetAccountByEmailAsync(email, cancellationToken);
        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default) =>
            inner.AddAccountAsync(input, cancellationToken);
        public async Task<AccountMetadata?> UpdateAccountAsync(
            string id,
            UpdateAccountInput updates,
            CancellationToken cancellationToken = default)
        {
            UpdateEntered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            throw new IOException("Injected delayed metadata commit failure.");
        }
        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default) =>
            inner.RemoveAccountAsync(id, cancellationToken);
        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default) =>
            inner.GetActiveAccountIdAsync(cancellationToken);
        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default) =>
            inner.SetActiveAccountIdAsync(id, cancellationToken);
        public Task<bool> CompareExchangeActiveAccountIdAsync(
            string? expectedId,
            string? newId,
            CancellationToken cancellationToken = default) =>
            inner.CompareExchangeActiveAccountIdAsync(expectedId, newId, cancellationToken);
    }
}
