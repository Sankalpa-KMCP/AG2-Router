using System.IO;
using System.Text;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Persistence;
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
    public async Task EnrollCurrentAccount_WhenWinCredIdentityDoesNotMatchCurrentAccount_ThrowsAccountEnrollmentException()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("real@example.com", "Developer");
        _winCredStore.Seed("gemini:antigravity", "foreign@example.com", Encoding.UTF8.GetBytes("{\"token\":\"fake\"}"));
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);

        var ex = await Assert.ThrowsAsync<AccountEnrollmentException>(() =>
            service.EnrollCurrentAccountAsync());

        Assert.Contains("does not match authenticated Antigravity account", ex.Message);
    }

    [Fact]
    public async Task EnrollCurrentAccount_WhenBlobMalformed_ThrowsAccountEnrollmentException()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("dev@example.com", "Developer");
        _winCredStore.Seed("gemini:antigravity", "dev@example.com", Encoding.UTF8.GetBytes("not-valid-json"));
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
        _winCredStore.Seed("gemini:antigravity", "dev@example.com", validBlob);

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
        _winCredStore.Seed("gemini:antigravity", "duplicate@example.com", blob1);

        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);

        // First enrollment
        var firstResult = await service.EnrollCurrentAccountAsync(new EnrollmentOptions(Priority: 1));
        Assert.True(firstResult.IsNew);
        string originalId = firstResult.Account.Id;

        // Second enrollment with updated options and new credential blob
        byte[] blob2 = Encoding.UTF8.GetBytes("{\"token\":\"token-2-refreshed\",\"auth_method\":\"oauth\"}");
        _winCredStore.Seed("gemini:antigravity", "duplicate@example.com", blob2);

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
    public async Task EnrollCurrentAccount_WithOptionsAlias_PersistsAlias()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("alias-test@example.com", "Dev");
        byte[] blob = Encoding.UTF8.GetBytes("{\"token\":\"test\"}");
        _winCredStore.Seed("gemini:antigravity", "alias-test@example.com", blob);

        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);
        var result = await service.EnrollCurrentAccountAsync(new EnrollmentOptions(Alias: "Primary Work"));

        Assert.True(result.Success);
        Assert.Equal("Primary Work", result.Account.Alias);

        var stored = await _accountStore.GetAccountAsync(result.Account.Id);
        Assert.NotNull(stored);
        Assert.Equal("Primary Work", stored.Alias);
    }

    [Fact]
    public async Task EnrollCurrentAccount_WhenVaultSaveFails_DoesNotExposeVaultedMetadata()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("vault-failure@example.com", "Synthetic");
        _winCredStore.Seed("gemini:antigravity", "vault-failure@example.com", Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
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
        _winCredStore.Seed("gemini:antigravity", "metadata-failure@example.com", Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
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
        _winCredStore.Seed("gemini:antigravity", "concurrent-enroll@example.com", Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
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
    public async Task EnrollCurrentAccount_DifferentAccountsShareOneEnrollmentCommitLane()
    {
        var trackingDpapi = new TrackingDpapiProvider();
        var vault = new SessionVault(_tempDir, trackingDpapi);
        var firstAdapter = new MockAG2Adapter
        {
            CurrentAccount = new AccountIdentityDto("first-lane@example.com", "First")
        };
        var secondAdapter = new MockAG2Adapter
        {
            CurrentAccount = new AccountIdentityDto("second-lane@example.com", "Second")
        };
        var firstCredStore = new InMemoryWinCredStore();
        firstCredStore.Seed(
            "gemini:antigravity",
            "first-lane@example.com",
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var secondCredStore = new InMemoryWinCredStore();
        secondCredStore.Seed(
            "gemini:antigravity",
            "second-lane@example.com",
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));

        var results = await Task.WhenAll(
            new AccountEnrollmentService(firstAdapter, firstCredStore, vault, _accountStore)
                .EnrollCurrentAccountAsync(),
            new AccountEnrollmentService(secondAdapter, secondCredStore, vault, _accountStore)
                .EnrollCurrentAccountAsync());

        Assert.Equal(2, results.Length);
        Assert.Equal(2, (await _accountStore.ListAccountsAsync()).Count);
        Assert.Equal(2, (await vault.ListStoredAccountIdsAsync()).Count);
        Assert.Equal(1, trackingDpapi.MaxConcurrentEncryptions);
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
        _winCredStore.Seed("gemini:antigravity", "existing-failure@example.com", Encoding.UTF8.GetBytes("{\"token\":\"replacement-synthetic\"}"));
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
        _winCredStore.Seed("gemini:antigravity", "failing-enrollment@example.com", Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
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
    public async Task EnrollmentCommit_PreservesConcurrentActiveSelectionWithoutRollingBackCoreCommit()
    {
        var previous = await _accountStore.AddAccountAsync(new CreateAccountInput(Email: "active-choice@example.com"));
        await _accountStore.SetActiveAccountIdAsync(previous.Id);
        _mockAdapter.CurrentAccount = new AccountIdentityDto("committed-enrollment@example.com", "Synthetic");
        _winCredStore.Seed(
            "gemini:antigravity",
            "committed-enrollment@example.com",
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var service = new AccountEnrollmentService(
            _mockAdapter,
            _winCredStore,
            _sessionVault,
            new RejectActiveCasAccountStore(_accountStore));

        var result = await service.EnrollCurrentAccountAsync();

        Assert.True(result.Success);
        Assert.True(result.Account.HasVaultedSession);
        Assert.True(await _sessionVault.HasSessionAsync(result.Account.Id));
        Assert.Equal(previous.Id, await _accountStore.GetActiveAccountIdAsync());
        Assert.Contains("preserved", result.Message, StringComparison.OrdinalIgnoreCase);
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
        _winCredStore.Seed("gemini:antigravity", "vault-race@example.com", enrollment);
        var blockingStore = new BlockingFailUpdateAccountStore(_accountStore);
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, blockingStore);

        Task<EnrollmentResult> enrollmentTask = service.EnrollCurrentAccountAsync();
        await blockingStore.UpdateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _sessionVault.SaveSessionAsync(account.Id, newer);
        blockingStore.ReleaseFailure();

        await Assert.ThrowsAsync<AccountEnrollmentException>(() => enrollmentTask);
        Assert.Equal(newer, await _sessionVault.GetSessionAsync(account.Id));
    }

    [Fact]
    public async Task EnrollmentCompensation_DoesNotDeleteConcurrentlyUpdatedPlaceholder()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("placeholder-race@example.com", "Synthetic");
        _winCredStore.Seed(
            "gemini:antigravity",
            "placeholder-race@example.com",
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var blockingStore = new BlockingFailUpdateAccountStore(_accountStore);
        var service = new AccountEnrollmentService(
            _mockAdapter,
            _winCredStore,
            _sessionVault,
            blockingStore);

        Task<EnrollmentResult> enrollment = service.EnrollCurrentAccountAsync();
        await blockingStore.UpdateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = await _accountStore.GetAccountByEmailAsync("placeholder-race@example.com");
        Assert.NotNull(pending);
        var concurrentlyUpdated = await _accountStore.UpdateAccountAsync(
            pending.Id,
            new UpdateAccountInput(Notes: "concurrent-crud"));
        Assert.NotNull(concurrentlyUpdated);
        blockingStore.ReleaseFailure();

        await Assert.ThrowsAsync<AccountEnrollmentException>(() => enrollment);
        var preserved = await _accountStore.GetAccountAsync(pending.Id);
        Assert.NotNull(preserved);
        Assert.Equal("concurrent-crud", preserved.Notes);
        Assert.False(preserved.HasVaultedSession);
        Assert.False(await _sessionVault.HasSessionAsync(pending.Id));
    }

    private sealed class ThrowingDpapiProvider : IDpapiProvider
    {
        public Task<byte[]> EncryptAsync(byte[] plaintext, CancellationToken cancellationToken = default) =>
            throw new DpapiException("Injected encryption failure.");

        public Task<byte[]> DecryptAsync(byte[] ciphertext, CancellationToken cancellationToken = default) =>
            throw new DpapiException("Injected decryption failure.");
    }

    private sealed class TrackingDpapiProvider : IDpapiProvider
    {
        private int _currentEncryptions;
        private int _maxConcurrentEncryptions;

        public int MaxConcurrentEncryptions => Volatile.Read(ref _maxConcurrentEncryptions);

        public async Task<byte[]> EncryptAsync(byte[] plaintext, CancellationToken cancellationToken = default)
        {
            int current = Interlocked.Increment(ref _currentEncryptions);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxConcurrentEncryptions);
            }
            while (current > observed &&
                   Interlocked.CompareExchange(ref _maxConcurrentEncryptions, current, observed) != observed);

            try
            {
                await Task.Delay(25, cancellationToken);
                return [.. Encoding.UTF8.GetBytes("TRACK:"), .. plaintext];
            }
            finally
            {
                Interlocked.Decrement(ref _currentEncryptions);
            }
        }

        public Task<byte[]> DecryptAsync(byte[] ciphertext, CancellationToken cancellationToken = default) =>
            Task.FromResult(ciphertext[Encoding.UTF8.GetByteCount("TRACK:")..]);
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
        public Task<bool> RemoveAccountIfUnchangedAsync(
            AccountMetadata expected,
            CancellationToken cancellationToken = default) =>
            inner.RemoveAccountIfUnchangedAsync(expected, cancellationToken);
        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default) =>
            inner.GetActiveAccountIdAsync(cancellationToken);
        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default) =>
            inner.SetActiveAccountIdAsync(id, cancellationToken);
        public Task<bool> CompareExchangeActiveAccountIdAsync(
            string? expectedId,
            string? newId,
            CancellationToken cancellationToken = default) =>
            inner.CompareExchangeActiveAccountIdAsync(expectedId, newId, cancellationToken);
        public Task<AccountMetadata?> TryFinalizeSwitchAsync(
            string? expectedActiveId, string targetId, UpdateAccountInput updates,
            CancellationToken cancellationToken = default) =>
            inner.TryFinalizeSwitchAsync(expectedActiveId, targetId, updates, cancellationToken);
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
        public Task<bool> RemoveAccountIfUnchangedAsync(
            AccountMetadata expected,
            CancellationToken cancellationToken = default) =>
            inner.RemoveAccountIfUnchangedAsync(expected, cancellationToken);
        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default) =>
            inner.GetActiveAccountIdAsync(cancellationToken);
        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default) =>
            inner.SetActiveAccountIdAsync(id, cancellationToken);
        public Task<bool> CompareExchangeActiveAccountIdAsync(
            string? expectedId,
            string? newId,
            CancellationToken cancellationToken = default) =>
            inner.CompareExchangeActiveAccountIdAsync(expectedId, newId, cancellationToken);
        public Task<AccountMetadata?> TryFinalizeSwitchAsync(
            string? expectedActiveId, string targetId, UpdateAccountInput updates,
            CancellationToken cancellationToken = default) =>
            inner.TryFinalizeSwitchAsync(expectedActiveId, targetId, updates, cancellationToken);
    }

    private sealed class RejectActiveCasAccountStore(IAccountStore inner) : IAccountStore
    {
        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
            inner.ListAccountsAsync(cancellationToken);
        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default) =>
            inner.GetAccountAsync(id, cancellationToken);
        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            inner.GetAccountByEmailAsync(email, cancellationToken);
        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default) =>
            inner.AddAccountAsync(input, cancellationToken);
        public Task<AccountMetadata?> UpdateAccountAsync(
            string id,
            UpdateAccountInput updates,
            CancellationToken cancellationToken = default) =>
            inner.UpdateAccountAsync(id, updates, cancellationToken);
        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default) =>
            inner.RemoveAccountAsync(id, cancellationToken);
        public Task<bool> RemoveAccountIfUnchangedAsync(
            AccountMetadata expected,
            CancellationToken cancellationToken = default) =>
            inner.RemoveAccountIfUnchangedAsync(expected, cancellationToken);
        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default) =>
            inner.GetActiveAccountIdAsync(cancellationToken);
        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default) =>
            inner.SetActiveAccountIdAsync(id, cancellationToken);
        public Task<bool> CompareExchangeActiveAccountIdAsync(
            string? expectedId,
            string? newId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<AccountMetadata?> TryFinalizeSwitchAsync(
            string? expectedActiveId, string targetId, UpdateAccountInput updates,
            CancellationToken cancellationToken = default) => Task.FromResult<AccountMetadata?>(null);
    }
}

public sealed class AccountRemovalServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ag2_remove_{Guid.NewGuid():N}");

    [Fact]
    public async Task RemovalRechecksActiveIdentityAfterAcquiringSwitchOwnership()
    {
        var store = new InMemoryAccountStore();
        var account = await store.AddAccountAsync(new CreateAccountInput("target@example.com"));
        var vault = new SessionVault(_directory, new FakeDpapiProvider());
        await vault.SaveSessionAsync(account.Id, [1, 2, 3]);
        var gate = PathLockRegistry.Get(vault.GetVaultPath() + ".switch");
        await gate.WaitAsync();
        try
        {
            var pending = new AccountRemovalService(store, vault).RemoveAsync(account.Id);
            Assert.False(pending.IsCompleted);
            await store.SetActiveAccountIdAsync(account.Id);
            gate.Release();
            await Assert.ThrowsAsync<AccountRemovalConflictException>(() => pending);
        }
        finally
        {
            if (gate.CurrentCount == 0) gate.Release();
        }
        Assert.NotNull(await store.GetAccountAsync(account.Id));
        Assert.True(await vault.HasSessionAsync(account.Id));
    }

    [Fact]
    public async Task MetadataWriteFailureRestoresRemovedVaultRecord()
    {
        Directory.CreateDirectory(_directory);
        var writer = new FailSecondWrite();
        var store = new LocalMetadataAccountStore(Path.Combine(_directory, "accounts.json"), writer);
        var account = await store.AddAccountAsync(new CreateAccountInput("target@example.com"));
        var vault = new SessionVault(Path.Combine(_directory, "vault"), new FakeDpapiProvider());
        await vault.SaveSessionAsync(account.Id, [1, 2, 3]);

        await Assert.ThrowsAsync<IOException>(() => new AccountRemovalService(store, vault).RemoveAsync(account.Id));
        Assert.NotNull(await store.GetAccountAsync(account.Id));
        Assert.Equal([1, 2, 3], await vault.GetSessionAsync(account.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FailSecondWrite : IDurableFileWriter
    {
        private readonly DurableFileWriter _inner = new();
        private int _writes;
        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _writes) == 2)
                throw new IOException("synthetic metadata removal failure");
            return _inner.WriteAtomicAsync(destinationPath, content, cancellationToken);
        }
    }
}
