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
    [Fact]
    public async Task CredentialRotationWithSameUsernameDoesNotCommitCurrentSession()
    {
        const string email = "rotation@example.com";
        _mockAdapter.CurrentAccount = new AccountIdentityDto(email, "Synthetic");
        _winCredStore.Seed("gemini:antigravity", email, Encoding.UTF8.GetBytes("{\"token\":\"old-synthetic\"}"));
        int reads = 0;
        _mockAdapter.GetCurrentAccountFunc = _ => {
            if (Interlocked.Increment(ref reads) == 2)
                _winCredStore.Seed("gemini:antigravity", email, Encoding.UTF8.GetBytes("{\"token\":\"rotated-synthetic\"}"));
            return Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(email, "Synthetic"));
        };

        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);
        await Assert.ThrowsAsync<AccountEnrollmentException>(() => service.EnrollCurrentAccountAsync());
        Assert.Null(await _accountStore.GetAccountByEmailAsync(email));
        Assert.Null(await _accountStore.GetActiveAccountIdAsync());
        Assert.Empty(await _sessionVault.ListStoredAccountIdsAsync());
    }

    [Fact]
    public async Task CredentialRotationRestoresExistingMetadataWithoutInventingVaultAvailability()
    {
        const string email = "existing-rotation@example.com";
        var previous = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: email, Name: "Original", Priority: 2, HasVaultedSession: false));
        _mockAdapter.CurrentAccount = new AccountIdentityDto(email, "Synthetic");
        _winCredStore.Seed("gemini:antigravity", email, Encoding.UTF8.GetBytes("{\"token\":\"old-synthetic\"}"));
        int reads = 0;
        _mockAdapter.GetCurrentAccountFunc = _ => {
            if (Interlocked.Increment(ref reads) == 2)
                _winCredStore.Seed("gemini:antigravity", email, Encoding.UTF8.GetBytes("{\"token\":\"rotated-synthetic\"}"));
            return Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(email, "Synthetic"));
        };

        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);
        await Assert.ThrowsAsync<AccountEnrollmentException>(() => service.EnrollCurrentAccountAsync(
            new EnrollmentOptions(Name: "Incorrect replacement", Priority: 9)));

        Assert.Equal(previous, await _accountStore.GetAccountAsync(previous.Id));
        Assert.Null(await _accountStore.GetActiveAccountIdAsync());
        Assert.False(await _sessionVault.HasSessionAsync(previous.Id));
    }

    [Fact]
    public async Task DeletionCannotPassEnrollmentFinalIdentityCommit()
    {
        const string email = "shared-ownership@example.com";
        _winCredStore.Seed("gemini:antigravity", email, Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        _mockAdapter.GetCurrentAccountFunc = async _ => {
            if (Interlocked.Increment(ref reads) == 2) {
                entered.TrySetResult();
                await release.Task;
            }
            return new AccountIdentityDto(email, "Synthetic");
        };
        var enrollment = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore)
            .EnrollCurrentAccountAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var account = await _accountStore.GetAccountByEmailAsync(email);
        Assert.NotNull(account);
        var deletion = new AccountRemovalService(_accountStore, _sessionVault).RemoveAsync(account.Id);
        Assert.False(deletion.IsCompleted);
        release.TrySetResult();
        var result = await enrollment.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.Success);
        await Assert.ThrowsAsync<AccountRemovalConflictException>(() => deletion.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(account.Id, await _accountStore.GetActiveAccountIdAsync());
        Assert.True(await _sessionVault.HasSessionAsync(account.Id));
    }

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

        var failure = await Assert.ThrowsAsync<AccountEnrollmentException>(() => failingService.EnrollCurrentAccountAsync());
        Assert.DoesNotContain("manual recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
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
        string newerVaultEnvelope = File.ReadAllText(_sessionVault.GetVaultPath());
        blockingStore.ReleaseFailure();

        var failure = await Assert.ThrowsAsync<AccountEnrollmentException>(() => enrollmentTask);
        Assert.Contains("manual recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(newerVaultEnvelope, File.ReadAllText(_sessionVault.GetVaultPath()));
        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => _sessionVault.GetSessionAsync(account.Id));
    }

    [Fact]
    public async Task MetadataFailureAndVaultRestoreFailureRequiresManualRecovery()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("restore-failure@example.com", "Synthetic");
        _winCredStore.Seed("gemini:antigravity", "restore-failure@example.com",
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var vault = new SessionVault(Path.Combine(_tempDir, "fault-vault"),
            new FakeDpapiProvider(), new FailSecondVaultWrite());
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, vault,
            new FailUpdateAccountStore(_accountStore));

        var failure = await Assert.ThrowsAsync<AccountEnrollmentException>(() => service.EnrollCurrentAccountAsync());

        Assert.Contains("manual recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
        var pending = await _accountStore.GetAccountByEmailAsync("restore-failure@example.com");
        Assert.NotNull(pending);
        Assert.Contains(pending.Id, File.ReadAllText(vault.GetVaultPath()));
        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => vault.HasSessionAsync(pending.Id));
    }

    [Fact]
    public async Task MetadataFailureAndUnreadableReadbackRequiresManualRecovery()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("readback-failure@example.com", "Synthetic");
        _winCredStore.Seed("gemini:antigravity", "readback-failure@example.com",
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var store = new FailUpdateAccountStore(_accountStore) { FailReadback = true };
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, store);

        var failure = await Assert.ThrowsAsync<AccountEnrollmentException>(() => service.EnrollCurrentAccountAsync());

        Assert.Contains("manual recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await _accountStore.GetAccountByEmailAsync("readback-failure@example.com"));
    }

    [Fact]
    public async Task MetadataFailureAndUnprovedConditionalRemovalRequiresManualRecovery()
    {
        _mockAdapter.CurrentAccount = new AccountIdentityDto("remove-failure@example.com", "Synthetic");
        _winCredStore.Seed("gemini:antigravity", "remove-failure@example.com",
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var store = new FailUpdateAccountStore(_accountStore) { ReturnFalseOnRemoval = true };
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, store);

        var failure = await Assert.ThrowsAsync<AccountEnrollmentException>(() => service.EnrollCurrentAccountAsync());

        Assert.Contains("manual recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
        var pending = await _accountStore.GetAccountByEmailAsync("remove-failure@example.com");
        Assert.NotNull(pending);
        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => _sessionVault.HasSessionAsync(pending.Id));
    }

    [Fact]
    public async Task MetadataWriterFailureAfterReplacementReportsUncertainCommit()
    {
        const string email = "replaced-metadata@example.com";
        _mockAdapter.CurrentAccount = new AccountIdentityDto(email, "Synthetic");
        _winCredStore.Seed("gemini:antigravity", email,
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var store = new LocalMetadataAccountStore(
            Path.Combine(_tempDir, "replaced-metadata.json"), new ThrowAfterNthWrite(2));
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, store);

        var failure = await Assert.ThrowsAsync<AccountEnrollmentException>(() => service.EnrollCurrentAccountAsync());

        Assert.Contains("manual recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
        var committed = await store.GetAccountByEmailAsync(email);
        Assert.NotNull(committed);
        Assert.True(committed.HasVaultedSession);
        Assert.Contains(committed.Id, File.ReadAllText(_sessionVault.GetVaultPath()));
        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => _sessionVault.HasSessionAsync(committed.Id));
    }

    [Fact]
    public async Task VaultWriterFailureAfterReplacementRetainsAccountRelationship()
    {
        const string email = "vault-after-replace@example.com";
        _mockAdapter.CurrentAccount = new AccountIdentityDto(email, "Synthetic");
        _winCredStore.Seed("gemini:antigravity", email,
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var vault = new SessionVault(Path.Combine(_tempDir, "vault-after-replace"),
            new FakeDpapiProvider(), new ThrowAfterNthWrite(1));

        var result = await new AccountEnrollmentService(_mockAdapter, _winCredStore, vault, _accountStore)
            .EnrollCurrentAccountAsync();

        Assert.True(result.Success);
        Assert.True((await _accountStore.GetAccountAsync(result.Account.Id))?.HasVaultedSession);
        Assert.True(await vault.HasSessionAsync(result.Account.Id));
    }

    [Fact]
    public async Task NonCompletingMetadataCommitQuarantinesLaterLifecycleCalls()
    {
        const string email = "metadata-timeout@example.com";
        _mockAdapter.CurrentAccount = new AccountIdentityDto(email, "Synthetic");
        _winCredStore.Seed("gemini:antigravity", email,
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var store = new FailUpdateAccountStore(_accountStore) { NeverCompleteUpdate = true };
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, store,
            TimeSpan.FromMilliseconds(60));

        var failure = await Assert.ThrowsAsync<AccountEnrollmentException>(
            () => service.EnrollCurrentAccountAsync().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("manual recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
        var second = await Assert.ThrowsAsync<AccountEnrollmentException>(() =>
            service.EnrollCurrentAccountAsync().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Contains("manual recovery", second.Message, StringComparison.OrdinalIgnoreCase);
        var gate = PathLockRegistry.Get(_sessionVault.GetVaultPath() + ".switch");
        Assert.False(await gate.WaitAsync(TimeSpan.FromMilliseconds(30)));
    }

    [Fact]
    public async Task LateMetadataCommitCannotOverlapNewEnrollmentAfterTimeout()
    {
        const string email = "late-metadata@example.com";
        _mockAdapter.CurrentAccount = new AccountIdentityDto(email, "Synthetic");
        _winCredStore.Seed("gemini:antigravity", email,
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var store = new FailUpdateAccountStore(_accountStore) { DelayedCommit = true };
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, store,
            TimeSpan.FromMilliseconds(70));

        try
        {
            var first = service.EnrollCurrentAccountAsync();
            await store.UpdateEntered.WaitAsync(TimeSpan.FromSeconds(2));
            var error = await Assert.ThrowsAsync<AccountEnrollmentException>(() =>
                first.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains("manual recovery", error.Message, StringComparison.OrdinalIgnoreCase);
            var pending = await _accountStore.GetAccountByEmailAsync(email);
            Assert.NotNull(pending);

            var later = await Assert.ThrowsAsync<AccountEnrollmentException>(() =>
                service.EnrollCurrentAccountAsync().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Contains("manual recovery", later.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False((await _accountStore.GetAccountAsync(pending.Id))?.HasVaultedSession);

            store.ReleaseUpdate();
            await store.UpdateFinished.WaitAsync(TimeSpan.FromSeconds(2));
            var gate = PathLockRegistry.Get(_sessionVault.GetVaultPath() + ".switch");
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));
            gate.Release();
            Assert.True((await _accountStore.GetAccountAsync(pending.Id))?.HasVaultedSession);
        }
        finally
        {
            store.ReleaseUpdate();
        }
    }

    [Fact]
    public async Task TimedOutReadCannotBeginNewMetadataMutationAfterOwnershipReleases()
    {
        const string email = "late-read@example.com";
        _mockAdapter.CurrentAccount = new AccountIdentityDto(email, "Synthetic");
        _winCredStore.Seed("gemini:antigravity", email,
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var store = new FailUpdateAccountStore(_accountStore) { DelayGetByEmail = true };
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, store,
            TimeSpan.FromMilliseconds(70));
        try
        {
            var first = service.EnrollCurrentAccountAsync();
            await store.ReadEntered.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<AccountEnrollmentException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, store.AddCount);
            store.ReleaseRead();
            var gate = PathLockRegistry.Get(_sessionVault.GetVaultPath() + ".switch");
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));
            gate.Release();
            Assert.Equal(0, store.AddCount);
            Assert.Null(await _accountStore.GetAccountByEmailAsync(email));
        }
        finally
        {
            store.ReleaseRead();
        }
    }

    [Fact]
    public async Task VaultTimeoutQuarantinePreventsLaterEnrollmentMetadataCreation()
    {
        const string email = "vault-quarantine@example.com";
        _mockAdapter.CurrentAccount = new AccountIdentityDto(email, "Synthetic");
        _winCredStore.Seed("gemini:antigravity", email,
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vault = new SessionVault(_tempDir, _fakeDpapi, new DelayedFirstVaultWrite(entered, release))
        {
            MutationTimeout = TimeSpan.FromMilliseconds(70)
        };
        try
        {
            var first = vault.SaveSessionAsync("unrelated", "synthetic"u8.ToArray());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<VaultMutationUncertainException>(
                () => first.WaitAsync(TimeSpan.FromSeconds(2)));

            var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, vault, _accountStore,
                TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAsync<AccountEnrollmentException>(
                () => service.EnrollCurrentAccountAsync().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Null(await _accountStore.GetAccountByEmailAsync(email));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class DelayedFirstVaultWrite(TaskCompletionSource entered, TaskCompletionSource release)
        : IDurableFileWriter
    {
        private readonly DurableFileWriter _inner = new();
        public async Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task;
            await _inner.WriteAtomicAsync(destinationPath, content, CancellationToken.None);
        }
    }

    [Fact]
    public async Task NonCompletingRotationRestoreReportsManualRecoveryAndReleasesOwnership()
    {
        const string email = "rotation-timeout@example.com";
        _mockAdapter.CurrentAccount = new AccountIdentityDto(email, "Synthetic");
        _winCredStore.Seed("gemini:antigravity", email,
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        int reads = 0;
        _mockAdapter.GetCurrentAccountFunc = _ =>
        {
            if (Interlocked.Increment(ref reads) == 2)
                _winCredStore.Seed("gemini:antigravity", email,
                    Encoding.UTF8.GetBytes("{\"token\":\"rotated\"}"));
            return Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(email, "Synthetic"));
        };
        var writer = new NeverCompleteNthWrite(2);
        var vault = new SessionVault(Path.Combine(_tempDir, "rotation-timeout"),
            new FakeDpapiProvider(), writer);
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, vault,
            _accountStore, TimeSpan.FromMilliseconds(60));

        var failure = await Assert.ThrowsAsync<AccountEnrollmentException>(
            () => service.EnrollCurrentAccountAsync().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("manual recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
        writer.Release();
        var vaultGate = PathLockRegistry.Get(vault.GetVaultPath());
        Assert.True(await vaultGate.WaitAsync(TimeSpan.FromSeconds(2)));
        vaultGate.Release();
        var gate = PathLockRegistry.Get(vault.GetVaultPath() + ".switch");
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(1)));
        gate.Release();
    }

    [Fact]
    public async Task ActiveSelectionWriterFailureAfterReplacementReportsCommittedSelection()
    {
        const string email = "replaced-active@example.com";
        _mockAdapter.CurrentAccount = new AccountIdentityDto(email, "Synthetic");
        _winCredStore.Seed("gemini:antigravity", email,
            Encoding.UTF8.GetBytes("{\"token\":\"synthetic\"}"));
        var store = new LocalMetadataAccountStore(
            Path.Combine(_tempDir, "replaced-active.json"), new ThrowAfterNthWrite(3));
        var service = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, store);

        var result = await service.EnrollCurrentAccountAsync();

        Assert.True(result.Success);
        Assert.Equal(result.Account.Id, await store.GetActiveAccountIdAsync());
        Assert.DoesNotContain("preserved", result.Message, StringComparison.OrdinalIgnoreCase);
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
        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => _sessionVault.HasSessionAsync(pending.Id));
    }

    private sealed class ThrowingDpapiProvider : IDpapiProvider
    {
        public Task<byte[]> EncryptAsync(byte[] plaintext, CancellationToken cancellationToken = default) =>
            throw new DpapiException("Injected encryption failure.");

        public Task<byte[]> DecryptAsync(byte[] ciphertext, CancellationToken cancellationToken = default) =>
            throw new DpapiException("Injected decryption failure.");
    }

    private sealed class FailSecondVaultWrite : IDurableFileWriter
    {
        private int _writes;
        private readonly DurableFileWriter _inner = new();

        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _writes) == 2)
                return Task.FromException(new IOException("Injected vault restoration write failure."));
            return _inner.WriteAtomicAsync(destinationPath, content, cancellationToken);
        }
    }

    private sealed class ThrowAfterNthWrite(int ordinal) : IDurableFileWriter
    {
        private int _writes;
        private readonly DurableFileWriter _inner = new();

        public async Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            await _inner.WriteAtomicAsync(destinationPath, content, cancellationToken);
            if (Interlocked.Increment(ref _writes) == ordinal)
                throw new IOException("Injected exception after durable replacement.");
        }
    }

    private sealed class NeverCompleteNthWrite(int ordinal) : IDurableFileWriter
    {
        private readonly DurableFileWriter _inner = new();
        private int _writes;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _writes) == ordinal
                ? _release.Task
                : _inner.WriteAtomicAsync(destinationPath, content, cancellationToken);
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
        public bool FailReadback { get; init; }
        public bool ReturnFalseOnRemoval { get; init; }
        public bool NeverCompleteUpdate { get; init; }
        public bool DelayedCommit { get; init; }
        public bool DelayGetByEmail { get; init; }
        public int AddCount { get; private set; }
        private readonly TaskCompletionSource _readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ReadEntered => _readEntered.Task;
        public void ReleaseRead() => _releaseRead.TrySetResult();
        private readonly TaskCompletionSource _updateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _updateFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task UpdateEntered => _updateEntered.Task;
        public Task UpdateFinished => _updateFinished.Task;
        public void ReleaseUpdate() => _releaseUpdate.TrySetResult();
        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default) =>
            inner.RestoreAccountIfUnchangedAsync(expectedCurrent, previous, cancellationToken);
        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
            inner.ListAccountsAsync(cancellationToken);
        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default) =>
            FailReadback ? Task.FromException<AccountMetadata?>(new IOException("Injected readback failure.")) :
                inner.GetAccountAsync(id, cancellationToken);
        public async Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            if (DelayGetByEmail)
            {
                _readEntered.TrySetResult();
                await _releaseRead.Task;
            }
            return await inner.GetAccountByEmailAsync(email, cancellationToken);
        }
        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default)
        {
            AddCount++;
            return inner.AddAccountAsync(input, cancellationToken);
        }
        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default) =>
            DelayedCommit ? CommitLateAsync(id, updates) : NeverCompleteUpdate
                ? new TaskCompletionSource<AccountMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously).Task
                : throw new IOException("Injected metadata commit failure.");
        private async Task<AccountMetadata?> CommitLateAsync(string id, UpdateAccountInput updates)
        {
            _updateEntered.TrySetResult();
            await _releaseUpdate.Task;
            try { return await inner.UpdateAccountAsync(id, updates, CancellationToken.None); }
            finally { _updateFinished.TrySetResult(); }
        }
        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default) =>
            inner.RemoveAccountAsync(id, cancellationToken);
        public Task<bool> RemoveAccountIfUnchangedAsync(
            AccountMetadata expected,
            CancellationToken cancellationToken = default) =>
            ReturnFalseOnRemoval ? Task.FromResult(false) :
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
        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default) =>
            inner.RestoreAccountIfUnchangedAsync(expectedCurrent, previous, cancellationToken);
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
        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default) =>
            inner.RestoreAccountIfUnchangedAsync(expectedCurrent, previous, cancellationToken);
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

    [Fact]
    public async Task MetadataWriteAndReadbackFailureIsExplicitAndRestoresVault()
    {
        Directory.CreateDirectory(_directory);
        var writer = new CorruptingFailSecondWrite();
        var store = new LocalMetadataAccountStore(Path.Combine(_directory, "accounts.json"), writer);
        var account = await store.AddAccountAsync(new CreateAccountInput("target@example.com"));
        var vault = new SessionVault(Path.Combine(_directory, "vault"), new FakeDpapiProvider());
        await vault.SaveSessionAsync(account.Id, [1, 2, 3]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AccountRemovalService(store, vault).RemoveAsync(account.Id));
        Assert.Contains("manual", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(account.Id, File.ReadAllText(vault.GetVaultPath()));
        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => vault.GetSessionAsync(account.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AccountRemovalService(store, vault).RemoveAsync(account.Id));
    }

    [Fact]
    public async Task UnprovedVaultRemovalQuarantinesLaterLifecycleAdmission()
    {
        var store = new InMemoryAccountStore();
        var account = await store.AddAccountAsync(new CreateAccountInput("uncertain-removal@example.com"));
        var vault = new SessionVault(Path.Combine(_directory, "uncertain-removal-vault"),
            new FakeDpapiProvider(), new ReplaceThenFailAndRejectRestoreWriter());
        await vault.SaveSessionAsync(account.Id, [1, 2, 3]);
        var service = new AccountRemovalService(store, vault);

        var failure = await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => service.RemoveAsync(account.Id));
        Assert.Contains("manual recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await store.GetAccountAsync(account.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(account.Id));
        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => vault.SaveSessionAsync(account.Id, [4, 5, 6]));
    }

    [Fact]
    public async Task NonCompletingMetadataReadbackAfterVaultRemovalFailsWithinDeadline()
    {
        var underlying = new InMemoryAccountStore();
        var account = await underlying.AddAccountAsync(new CreateAccountInput("hung-readback@example.com"));
        var vault = new SessionVault(Path.Combine(_directory, "hung-readback-vault"), new FakeDpapiProvider());
        await vault.SaveSessionAsync(account.Id, [1, 2, 3]);
        var store = new HangingRemovalStore(underlying) { HangReadback = true };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AccountRemovalService(store, vault, TimeSpan.FromMilliseconds(60))
                .RemoveAsync(account.Id).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("manual", failure.Message, StringComparison.OrdinalIgnoreCase);
        var gate = PathLockRegistry.Get(vault.GetVaultPath() + ".switch");
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(1)));
        gate.Release();
    }

    [Fact]
    public async Task NonCompletingVaultRestoreAfterMetadataFailureFailsWithinDeadline()
    {
        var underlying = new InMemoryAccountStore();
        var account = await underlying.AddAccountAsync(new CreateAccountInput("hung-restore@example.com"));
        var writer = new NeverCompleteThirdWrite();
        var vault = new SessionVault(Path.Combine(_directory, "hung-restore-vault"),
            new FakeDpapiProvider(), writer);
        await vault.SaveSessionAsync(account.Id, [1, 2, 3]);
        var store = new HangingRemovalStore(underlying);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AccountRemovalService(store, vault, TimeSpan.FromMilliseconds(60))
                .RemoveAsync(account.Id).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("manual recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
        writer.Release();
        var vaultGate = PathLockRegistry.Get(vault.GetVaultPath());
        Assert.True(await vaultGate.WaitAsync(TimeSpan.FromSeconds(2)));
        vaultGate.Release();
        var gate = PathLockRegistry.Get(vault.GetVaultPath() + ".switch");
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(1)));
        gate.Release();
    }

    [Fact]
    public async Task TimedOutVaultRemovalRetainsVaultOwnershipUntilLateRecoveryWriteFinishes()
    {
        var store = new InMemoryAccountStore();
        var account = await store.AddAccountAsync(new CreateAccountInput("late-recovery@example.com"));
        var writer = new ReplaceFailThenDelayedRestoreWriter();
        var vault = new SessionVault(Path.Combine(_directory, "late-recovery-vault"),
            new FakeDpapiProvider(), writer);
        await vault.SaveSessionAsync(account.Id, [1, 2, 3]);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new AccountRemovalService(store, vault, TimeSpan.FromMilliseconds(60))
                    .RemoveAsync(account.Id).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains("manual recovery", error.Message, StringComparison.OrdinalIgnoreCase);
            await writer.RestoreStarted.WaitAsync(TimeSpan.FromSeconds(2));

            // The late writer ignores cancellation. No newer vault mutation may
            // be admitted while its durable outcome is unresolved.
            var unavailable = await Assert.ThrowsAsync<VaultMutationUncertainException>(() =>
                vault.SaveSessionAsync(account.Id, [9, 8, 7]).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Contains("manual recovery", unavailable.Message, StringComparison.OrdinalIgnoreCase);
            writer.Release();
            await writer.RestoreFinished.WaitAsync(TimeSpan.FromSeconds(2));
            var stillUnavailable = await Assert.ThrowsAsync<VaultMutationUncertainException>(() =>
                vault.SaveSessionAsync(account.Id, [9, 8, 7]).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Contains("manual recovery", stillUnavailable.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            writer.Release();
        }
        var vaultGate = PathLockRegistry.Get(vault.GetVaultPath());
        Assert.True(await vaultGate.WaitAsync(TimeSpan.FromSeconds(2)));
        vaultGate.Release();
    }

    [Fact]
    public async Task LateMetadataRemovalCannotRaceCompensationOrLaterLifecycleOperation()
    {
        var underlying = new InMemoryAccountStore();
        var account = await underlying.AddAccountAsync(new CreateAccountInput("late-removal@example.com"));
        var vault = new SessionVault(Path.Combine(_directory, "late-metadata-removal"),
            new FakeDpapiProvider());
        await vault.SaveSessionAsync(account.Id, [1, 2, 3]);
        var store = new HangingRemovalStore(underlying) { DelayedRemoval = true };
        var service = new AccountRemovalService(store, vault, TimeSpan.FromMilliseconds(70));

        try
        {
            var first = service.RemoveAsync(account.Id);
            await store.RemovalEntered.WaitAsync(TimeSpan.FromSeconds(2));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                first.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains("manual recovery", error.Message, StringComparison.OrdinalIgnoreCase);

            var later = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RemoveAsync(account.Id).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Contains("manual recovery", later.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(await underlying.GetAccountAsync(account.Id));
            store.ReleaseRemoval();
            await store.RemovalFinished.WaitAsync(TimeSpan.FromSeconds(2));
            var gate = PathLockRegistry.Get(vault.GetVaultPath() + ".switch");
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));
            gate.Release();
            Assert.Null(await underlying.GetAccountAsync(account.Id));
            // No compensation ran concurrently with the original metadata commit.
            Assert.DoesNotContain(account.Id, await File.ReadAllTextAsync(vault.GetVaultPath()));
        }
        finally
        {
            store.ReleaseRemoval();
        }
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

    private sealed class ReplaceThenFailAndRejectRestoreWriter : IDurableFileWriter
    {
        private readonly DurableFileWriter _inner = new();
        private int _writes;
        public async Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _writes);
            if (call == 3) throw new IOException("synthetic restoration failure");
            await _inner.WriteAtomicAsync(destinationPath, content, cancellationToken);
            if (call == 2) throw new IOException("synthetic post-replacement removal failure");
        }
    }

    private sealed class CorruptingFailSecondWrite : IDurableFileWriter
    {
        private readonly DurableFileWriter _inner = new();
        private int _writes;
        public async Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _writes) == 2)
            {
                await File.WriteAllTextAsync(destinationPath, "{invalid synthetic json", cancellationToken);
                throw new IOException("synthetic uncertain metadata replacement");
            }
            await _inner.WriteAtomicAsync(destinationPath, content, cancellationToken);
        }
    }

    private sealed class NeverCompleteThirdWrite : IDurableFileWriter
    {
        private int _writes;
        private readonly DurableFileWriter _inner = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _writes) == 3
                ? _release.Task
                : _inner.WriteAtomicAsync(destinationPath, content, cancellationToken);
    }

    private sealed class ReplaceFailThenDelayedRestoreWriter : IDurableFileWriter
    {
        private readonly DurableFileWriter _inner = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _restoreStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _restoreFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        public Task RestoreStarted => _restoreStarted.Task;
        public Task RestoreFinished => _restoreFinished.Task;
        public void Release() => _release.TrySetResult();

        public async Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            int write = Interlocked.Increment(ref _writes);
            if (write == 3)
            {
                _restoreStarted.TrySetResult();
                await _release.Task;
                await _inner.WriteAtomicAsync(destinationPath, content, CancellationToken.None);
                _restoreFinished.TrySetResult();
                return;
            }
            await _inner.WriteAtomicAsync(destinationPath, content, cancellationToken);
            if (write == 2)
                throw new IOException("Injected post-replacement removal failure.");
        }
    }

    private sealed class HangingRemovalStore(IAccountStore inner) : IAccountStore
    {
        private int _readCount;
        public bool HangReadback { get; init; }
        public bool DelayedRemoval { get; init; }
        private readonly TaskCompletionSource _removalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRemoval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _removalFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RemovalEntered => _removalEntered.Task;
        public Task RemovalFinished => _removalFinished.Task;
        public void ReleaseRemoval() => _releaseRemoval.TrySetResult();
        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default) =>
            HangReadback && Interlocked.Increment(ref _readCount) >= 2
                ? new TaskCompletionSource<AccountMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously).Task
                : inner.GetAccountAsync(id, cancellationToken);
        public Task<bool> RemoveAccountIfUnchangedAsync(AccountMetadata expected, CancellationToken cancellationToken = default) =>
            DelayedRemoval ? RemoveLateAsync(expected) :
                Task.FromException<bool>(new IOException("synthetic metadata removal failure"));
        private async Task<bool> RemoveLateAsync(AccountMetadata expected)
        {
            _removalEntered.TrySetResult();
            await _releaseRemoval.Task;
            try { return await inner.RemoveAccountIfUnchangedAsync(expected, CancellationToken.None); }
            finally { _removalFinished.TrySetResult(); }
        }
        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default) => inner.ListAccountsAsync(cancellationToken);
        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default) => inner.GetAccountByEmailAsync(email, cancellationToken);
        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default) => inner.AddAccountAsync(input, cancellationToken);
        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput input, CancellationToken cancellationToken = default) => inner.UpdateAccountAsync(id, input, cancellationToken);
        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default) => inner.RemoveAccountAsync(id, cancellationToken);
        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expected, AccountMetadata previous, CancellationToken cancellationToken = default) => inner.RestoreAccountIfUnchangedAsync(expected, previous, cancellationToken);
        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default) => inner.GetActiveAccountIdAsync(cancellationToken);
        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default) => inner.SetActiveAccountIdAsync(id, cancellationToken);
        public Task<bool> CompareExchangeActiveAccountIdAsync(string? expected, string? next, CancellationToken cancellationToken = default) => inner.CompareExchangeActiveAccountIdAsync(expected, next, cancellationToken);
        public Task<AccountMetadata?> TryFinalizeSwitchAsync(string? expected, string targetId, UpdateAccountInput input, CancellationToken cancellationToken = default) => inner.TryFinalizeSwitchAsync(expected, targetId, input, cancellationToken);
    }
}
