using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Switching;
using AG2Router.AG2.Vault;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Comprehensive synthetic unit tests for the proof-based journal-resolution engine
/// in NativeAccountSwitchCoordinator (TODO-003 Step 4A / ADR-001 Operator Resolution).
/// Covers all 23 mandated test scenarios with strict synthetic fixtures and zero OS mutation.
/// </summary>
[Collection("SwitchCoordinator")]
public sealed class NativeAccountSwitchCoordinatorResolutionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ag2_resolution_test_{Guid.NewGuid():N}");
    private readonly TestAccountStore _accounts = new();
    private readonly FakeDpapiProvider _dpapi = new();
    private readonly TestCredentialStore _credentials;
    private readonly TestAdapter _adapter;
    private readonly TestProcessLifecycle _process;
    private readonly TestJournalStore _journal;
    private readonly SessionVault _vault;
    private readonly List<string> _globalTimeline = [];
    private AccountMetadata? _source;
    private AccountMetadata? _target;

    public NativeAccountSwitchCoordinatorResolutionTests()
    {
        Directory.CreateDirectory(_tempDir);
        _vault = new SessionVault(_tempDir, _dpapi);
        _credentials = new TestCredentialStore(_globalTimeline);
        _adapter = new TestAdapter(_globalTimeline);
        _process = new TestProcessLifecycle(_globalTimeline);
        _journal = new TestJournalStore(Path.Combine(_tempDir, "switch-journal.json"), _globalTimeline);
    }

    public void Dispose()
    {
        _credentials.Clear();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Resolution_LeaseCleanup_ReleasesSamePathAndPreservesBodyResult(bool bodyFails, bool cleanupFails)
    {
        var coordinator = CreateCoordinator();
        string resource = _vault.GetVaultPath() + ".switch";
        LeaseCleanupTestLease? injected = null;
        coordinator.AcquireRecoveryLeaseAsync = async (path, token) =>
            injected = new LeaseCleanupTestLease(await CrossProcessFileLease.AcquireAsync(path, token), path, cleanupFails);
        if (bodyFails) _journal.ReadBehavior = _ => throw new IOException("Primary journal read failure.");

        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(bodyFails || cleanupFails ? JournalResolutionStatus.PersistenceFailure :
            JournalResolutionStatus.NoJournal, result.Status);
        Assert.Equal(bodyFails ? "IO_ERROR" : cleanupFails ? "LEASE_CLEANUP_FAILED" : null, result.ReasonCode);
        if (bodyFails) Assert.Contains("I/O error", result.Message);
        if (cleanupFails) Assert.Contains("Lease cleanup also failed", result.Message);
        Assert.Equal(1, injected!.DisposeCount);
        Assert.Equal(1, NativeAccountSwitchCoordinator.SwitchGateForTest.CurrentCount);
        await LeaseCleanupTestLease.AssertReacquirableAsync(resource);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Resolution_LeaseCleanup_PreservesThrownFailureAndCancellation(bool cancel, bool cleanupFails)
    {
        await SeedJournalAsync(SwitchJournalState.RECORDED, "synthetic-source", "synthetic-target");
        using var cts = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException(cts.Token) : new IOException("Primary delete failure.");
        _journal.DeleteIfUnchangedBehavior = (_, _) =>
        {
            if (cancel) cts.Cancel();
            return Task.FromException<SwitchJournalDeleteResult>(primary);
        };
        var coordinator = CreateCoordinator();
        string resource = _vault.GetVaultPath() + ".switch";
        LeaseCleanupTestLease? injected = null;
        coordinator.AcquireRecoveryLeaseAsync = async (path, token) =>
            injected = new LeaseCleanupTestLease(await CrossProcessFileLease.AcquireAsync(path, token), path, cleanupFails);

        var error = await Record.ExceptionAsync(() => coordinator.ResolveQuarantinedJournalAsync(cts.Token));

        if (cleanupFails && !cancel)
        {
            var aggregate = Assert.IsType<AggregateException>(error);
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            Assert.Same(injected!.Failure, aggregate.InnerExceptions[1]);
        }
        else
        {
            Assert.Same(primary, error);
            if (cancel)
            {
                Assert.Equal(cts.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken);
                if (cleanupFails) Assert.Same(injected!.Failure, error!.Data["LeaseCleanupFailure"]);
            }
        }
        Assert.Equal(1, injected!.DisposeCount);
        Assert.Equal(1, NativeAccountSwitchCoordinator.SwitchGateForTest.CurrentCount);
        await LeaseCleanupTestLease.AssertReacquirableAsync(resource);
    }

    private async Task SeedAccountsAsync(
        bool vaultTarget = true,
        bool vaultSource = true,
        bool validateTarget = true,
        bool validateSource = true)
    {
        _source = await _accounts.AddAccountAsync(new CreateAccountInput(
            Email: "source@example.com", HasVaultedSession: vaultSource));
        _target = await _accounts.AddAccountAsync(new CreateAccountInput(
            Email: "target@example.com", HasVaultedSession: vaultTarget));

        if (validateSource)
        {
            _source = await _accounts.UpdateAccountAsync(_source.Id, new UpdateAccountInput(ValidationStatus: AccountValidationStatus.Valid)) ?? _source;
        }
        if (validateTarget)
        {
            _target = await _accounts.UpdateAccountAsync(_target.Id, new UpdateAccountInput(ValidationStatus: AccountValidationStatus.Valid)) ?? _target;
        }

        await _accounts.SetActiveAccountIdAsync(_target.Id);

        if (vaultSource)
        {
            await _vault.SaveSessionAsync(_source.Id, Encoding.UTF8.GetBytes("source-secret-payload"));
        }
        if (vaultTarget)
        {
            await _vault.SaveSessionAsync(_target.Id, Encoding.UTF8.GetBytes("target-secret-payload"));
        }

        _credentials.Set(CreateCredential(_target.Email, "target-secret-payload"));
        _adapter.Identity = new AccountIdentityDto(_target.Email);
    }

    private NativeAccountSwitchCoordinator CreateCoordinator(
        TimeSpan? transactionTimeout = null) =>
        new(
            _accounts,
            _vault,
            _credentials,
            _credentials,
            _adapter,
            _process,
            _journal,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(500),
            transactionTimeout: transactionTimeout ?? TimeSpan.FromSeconds(5));

    private static WinCredEntry CreateCredential(string user, string secret) =>
        new("gemini:antigravity", 1, user, 2, Encoding.UTF8.GetBytes(secret));

    private async Task SeedJournalAsync(
        SwitchJournalState state,
        string sourceId,
        string targetId,
        string? transactionId = null,
        string? reason = null)
    {
        var entry = new SwitchJournalEntry
        {
            Magic = SwitchJournalConstants.Magic,
            SchemaVersion = SwitchJournalConstants.CurrentSchemaVersion,
            TransactionId = transactionId ?? Guid.NewGuid().ToString("D"),
            State = state,
            SourceAccountId = sourceId,
            TargetAccountId = targetId,
            UpdatedAt = DateTimeOffset.UtcNow,
            QuarantineReasonCode = reason
        };
        await _journal.WriteEntryAsync(entry);
    }

    // 1. ResolveQuarantinedJournal_WhenNoJournal_ReturnsNoJournal
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenNoJournal_ReturnsNoJournal()
    {
        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.NoJournal, result.Status);
        Assert.Equal("No switch journal present.", result.Message);
        Assert.False(result.RestartRequired);
        Assert.False(File.Exists(_journal.JournalFilePath));
    }

    // 2. ResolveQuarantinedJournal_WhenJournalCorrupt_ReturnsNotResolvable_AndPreservesFile
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenJournalCorrupt_ReturnsNotResolvable_AndPreservesFile()
    {
        const string corruptBytes = "{\"magic\":\"AG2_SWJ1\",\"schemaVersion\":1,BROKEN_JSON";
        await File.WriteAllTextAsync(_journal.JournalFilePath, corruptBytes, Encoding.UTF8);

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.NotResolvable, result.Status);
        Assert.Equal("CORRUPT_JOURNAL", result.ReasonCode);
        Assert.False(result.RestartRequired);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(corruptBytes, await File.ReadAllTextAsync(_journal.JournalFilePath, Encoding.UTF8));
        Assert.Equal(JournalRecoveryStates.NotResolvable, coordinator.GetStatus().JournalRecoveryState);
    }

    // 2b. ResolveQuarantinedJournal_WhenJournalHasWrongPropertyTokenKinds_ReturnsNotResolvable_AndPreservesFile (F05)
    [Theory]
    [InlineData("{\"magic\":\"AG2SWITCHJRNL\",\"schemaVersion\":\"1\",\"transactionId\":\"11111111-2222-3333-4444-555555555555\",\"state\":\"RECORDED\",\"updatedAt\":\"2026-09-27T12:00:00Z\",\"sourceAccountId\":\"acc-1\",\"targetAccountId\":\"acc-2\"}")]
    [InlineData("{\"magic\":\"AG2SWITCHJRNL\",\"schemaVersion\":1,\"transactionId\":\"11111111-2222-3333-4444-555555555555\",\"state\":\"RECORDED\",\"updatedAt\":1234567890,\"sourceAccountId\":\"acc-1\",\"targetAccountId\":\"acc-2\"}")]
    public async Task ResolveQuarantinedJournal_WhenJournalHasWrongPropertyTokenKinds_ReturnsNotResolvable_AndPreservesFile(string badTokenKindJournal)
    {
        await File.WriteAllTextAsync(_journal.JournalFilePath, badTokenKindJournal, Encoding.UTF8);

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.NotResolvable, result.Status);
        Assert.Equal("CORRUPT_JOURNAL", result.ReasonCode);
        Assert.False(result.RestartRequired);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(badTokenKindJournal, await File.ReadAllTextAsync(_journal.JournalFilePath, Encoding.UTF8));
        Assert.Equal(JournalRecoveryStates.NotResolvable, coordinator.GetStatus().JournalRecoveryState);
    }

    // 3. ResolveQuarantinedJournal_WhenJournalUnsupportedVersion_ReturnsNotResolvable_AndPreservesFile
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenJournalUnsupportedVersion_ReturnsNotResolvable_AndPreservesFile()
    {
        string badVersionJson = JsonSerializer.Serialize(new
        {
            magic = SwitchJournalConstants.Magic,
            schemaVersion = 999,
            transactionId = "tx_future",
            state = "RECORDED"
        });
        await File.WriteAllTextAsync(_journal.JournalFilePath, badVersionJson, Encoding.UTF8);

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.NotResolvable, result.Status);
        Assert.Equal("UNSUPPORTED_VERSION", result.ReasonCode);
        Assert.False(result.RestartRequired);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(badVersionJson, await File.ReadAllTextAsync(_journal.JournalFilePath, Encoding.UTF8));
        Assert.Equal(JournalRecoveryStates.NotResolvable, coordinator.GetStatus().JournalRecoveryState);
    }

    // 4. ResolveQuarantinedJournal_WhenJournalReadIoError_ReturnsPersistenceFailure
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenJournalReadIoError_ReturnsPersistenceFailure()
    {
        _journal.ReadBehavior = ct => throw new IOException("Disk failure during initial journal read");

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.PersistenceFailure, result.Status);
        Assert.Equal("IO_ERROR", result.ReasonCode);
        Assert.Equal("An I/O error occurred while accessing the switch journal.", result.Message);
        Assert.DoesNotContain("Disk failure", result.Message);
    }

    // 5. ResolveQuarantinedJournal_WhenCoherentOnTarget_DeletesJournalAndRequiresRestart
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenCoherentOnTarget_DeletesJournalAndRequiresRestart()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);
        Assert.True(File.Exists(_journal.JournalFilePath));

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);
        Assert.Equal(_target.Id, result.CoherentAccountId);
        Assert.True(result.RestartRequired);
        Assert.False(File.Exists(_journal.JournalFilePath));
    }

    // 6. ResolveQuarantinedJournal_WhenCoherentOnSource_DeletesJournalAndRequiresRestart
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenCoherentOnSource_DeletesJournalAndRequiresRestart()
    {
        await SeedAccountsAsync();
        await _accounts.SetActiveAccountIdAsync(_source!.Id);
        _credentials.Set(CreateCredential(_source.Email, "source-secret-payload"));
        _adapter.Identity = new AccountIdentityDto(_source.Email);
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);
        Assert.Equal(_source.Id, result.CoherentAccountId);
        Assert.True(result.RestartRequired);
        Assert.False(File.Exists(_journal.JournalFilePath));
    }

    // 7. ResolveQuarantinedJournal_WhenCoherentOnNewerAccount_DeletesObsoleteJournal
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenCoherentOnNewerAccount_DeletesObsoleteJournal()
    {
        await SeedAccountsAsync();
        var third = await _accounts.AddAccountAsync(new CreateAccountInput(
            Email: "third@example.com", HasVaultedSession: true));
        third = await _accounts.UpdateAccountAsync(third.Id, new UpdateAccountInput(ValidationStatus: AccountValidationStatus.Valid)) ?? third;
        await _accounts.SetActiveAccountIdAsync(third.Id);
        await _vault.SaveSessionAsync(third.Id, Encoding.UTF8.GetBytes("third-secret-payload"));
        _credentials.Set(CreateCredential(third.Email, "third-secret-payload"));
        _adapter.Identity = new AccountIdentityDto(third.Email);

        // Stale journal between source and target
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);
        Assert.Equal(third.Id, result.CoherentAccountId);
        Assert.True(result.RestartRequired);
        Assert.False(File.Exists(_journal.JournalFilePath));
    }

    // 8. ResolveQuarantinedJournal_WhenLiveIdentityUnavailable_FailsClosed
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenLiveIdentityUnavailable_FailsClosed()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);
        _adapter.Identity = null;

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("LIVE_IDENTITY_UNAVAILABLE", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 9. ResolveQuarantinedJournal_WhenLiveIdentityMismatched_FailsClosed
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenLiveIdentityMismatched_FailsClosed()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);
        _adapter.Identity = new AccountIdentityDto("unrelated@example.com");

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("LIVE_IDENTITY_MISMATCH", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 10. ResolveQuarantinedJournal_WhenActiveAccountMissingFromMetadata_FailsClosed
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenActiveAccountMissingFromMetadata_FailsClosed()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);
        _accounts.GetActiveAccountIdBehavior = ct => Task.FromResult<string?>(null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("NO_ACTIVE_ACCOUNT", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 11. ResolveQuarantinedJournal_WhenActiveAccountNotEnrolledOrUnvaulted_FailsClosed
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenActiveAccountNotEnrolledOrUnvaulted_FailsClosed()
    {
        await SeedAccountsAsync(vaultTarget: false);
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("ACCOUNT_NOT_ENROLLED", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 12. ResolveQuarantinedJournal_WhenWinCredMissing_FailsClosed
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenWinCredMissing_FailsClosed()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);
        _credentials.Clear();

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("CREDENTIAL_MISSING", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 13. ResolveQuarantinedJournal_WhenVaultSessionMissing_FailsClosed
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenVaultSessionMissing_FailsClosed()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);
        await _vault.RemoveSessionAsync(_target!.Id);

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("VAULT_SESSION_MISSING", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 14. ResolveQuarantinedJournal_WhenWinCredAndVaultMismatched_FailsClosed
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenWinCredAndVaultMismatched_FailsClosed()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);
        _credentials.Set(CreateCredential(_target!.Email, "different-mismatched-secret"));

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("CREDENTIAL_MISMATCH", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 15. ResolveQuarantinedJournal_SecretMemoryHygiene_ZeroesBuffers
    [Fact]
    public async Task ResolveQuarantinedJournal_SecretMemoryHygiene_ZeroesBuffers()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        byte[] firstBuffer = Encoding.UTF8.GetBytes("target-secret-payload");
        byte[] secondBuffer = Encoding.UTF8.GetBytes("target-secret-payload");
        int readCount = 0;
        _credentials.ReadBehavior = ct =>
        {
            readCount++;
            byte[] buf = readCount == 1 ? firstBuffer : secondBuffer;
            return Task.FromResult<WinCredEntry?>(
                new WinCredEntry("gemini:antigravity", 1, _target!.Email, 2, buf));
        };

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);
        Assert.All(firstBuffer, b => Assert.Equal(0, b));
        Assert.All(secondBuffer, b => Assert.Equal(0, b));
    }

    // 16. ResolveQuarantinedJournal_PreDeleteRace_JournalChanged_AbortsDeletion
    [Fact]
    public async Task ResolveQuarantinedJournal_PreDeleteRace_JournalChanged_AbortsDeletion()
    {
        await SeedAccountsAsync();
        string txId = Guid.NewGuid().ToString("D");
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id, transactionId: txId);

        int readCount = 0;
        _adapter.GetCurrentAccountBehavior = async ct =>
        {
            readCount++;
            if (readCount == 2)
            {
                // Concurrently modified on disk during second proof right before delete
                await SeedJournalAsync(SwitchJournalState.ROLLING_BACK, _source!.Id, _target!.Id, transactionId: txId);
            }
            return new AccountIdentityDto(_target!.Email);
        };

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("CONCURRENT_MUTATION", result.ReasonCode);
        Assert.Equal(0, _journal.DeleteCount);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 17. ResolveQuarantinedJournal_PreDeleteRace_MetadataChanged_AbortsDeletion
    [Fact]
    public async Task ResolveQuarantinedJournal_PreDeleteRace_MetadataChanged_AbortsDeletion()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        // Prepare source account to be fully coherent so final proof succeeds on source
        int accountReadCount = 0;
        _accounts.GetActiveAccountIdBehavior = ct =>
        {
            accountReadCount++;
            if (accountReadCount == 1)
            {
                return Task.FromResult<string?>(_target!.Id);
            }
            // Changed right before delete to coherent source
            return Task.FromResult<string?>(_source!.Id);
        };

        _adapter.GetCurrentAccountBehavior = ct =>
        {
            return Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(accountReadCount <= 1 ? _target!.Email : _source!.Email));
        };

        _credentials.ReadBehavior = ct =>
        {
            string email = accountReadCount <= 1 ? _target!.Email : _source!.Email;
            string secret = accountReadCount <= 1 ? "target-secret-payload" : "source-secret-payload";
            return Task.FromResult<WinCredEntry?>(CreateCredential(email, secret));
        };

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("CONCURRENT_MUTATION", result.ReasonCode);
        Assert.Equal(0, _journal.DeleteCount);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 18. ResolveQuarantinedJournal_PreDeleteRace_LiveIdentityChanged_AbortsDeletion
    [Fact]
    public async Task ResolveQuarantinedJournal_PreDeleteRace_LiveIdentityChanged_AbortsDeletion()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        int readCount = 0;
        _adapter.GetCurrentAccountBehavior = ct =>
        {
            readCount++;
            if (readCount == 1)
            {
                return Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(_target!.Email));
            }
            // Changed right before delete
            return Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto("attacker@example.com"));
        };

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("LIVE_IDENTITY_MISMATCH", result.ReasonCode);
        Assert.Equal(0, _journal.DeleteCount);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 19. ResolveQuarantinedJournal_DeleteFailure_ReturnsPersistenceFailure
    [Fact]
    public async Task ResolveQuarantinedJournal_DeleteFailure_ReturnsPersistenceFailure()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);
        _journal.FailOnDelete = true;

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.PersistenceFailure, result.Status);
        Assert.Equal("DELETE_FAILED", result.ReasonCode);
        Assert.True(result.RestartRequired);
        Assert.Equal(_target!.Id, result.CoherentAccountId);
    }

    // 20. ResolveQuarantinedJournal_InProcessQuarantineRemainsMarkedUntilRestart
    [Fact]
    public async Task ResolveQuarantinedJournal_InProcessQuarantineRemainsMarkedUntilRestart()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        // Mark in-process quarantine (e.g. from prior failed switch or startup check)
        _vault.QuarantineUnresolvedMutation();
        Assert.True(_vault.IsQuarantined);

        var coordinator = CreateCoordinator();
        var statusBefore = coordinator.GetStatus();
        Assert.Equal(NativeSwitchStates.Failed, statusBefore.CurrentState);

        var result = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);

        // Current process remains quarantined (no in-process unmark primitive per ADR-001)
        Assert.True(_vault.IsQuarantined);
        var statusAfter = coordinator.GetStatus();
        Assert.Equal(NativeSwitchStates.Failed, statusAfter.CurrentState);
    }

    // 21. ResolveQuarantinedJournal_NextStartupStartsClean
    [Fact]
    public async Task ResolveQuarantinedJournal_NextStartupStartsClean()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));

        // Simulate next application startup with fresh coordinator
        var nextCoordinator = CreateCoordinator();
        var startupResult = await nextCoordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Clean, startupResult.Status);
    }

    // 22. ResolveQuarantinedJournal_NeverCallsTryFinalizeSwitchAsyncOrProcessMutations
    [Fact]
    public async Task ResolveQuarantinedJournal_NeverCallsTryFinalizeSwitchAsyncOrProcessMutations()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _process.LaunchCount);
        Assert.Equal(0, _process.QuiesceCount);
        Assert.Equal(0, _process.RestoreCount);
        Assert.Equal(0, _credentials.WriteCount);
    }

    // 23. ResolveQuarantinedJournal_RetainsMutationOwnershipUntilSettled
    [Fact]
    public async Task ResolveQuarantinedJournal_RetainsMutationOwnershipUntilSettled()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var deleteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _journal.DeleteBehavior = async ct =>
        {
            deleteEntered.TrySetResult();
            await resumeDelete.Task;
        };

        var coordinator = CreateCoordinator();
        var resolveTask = coordinator.ResolveQuarantinedJournalAsync();
        await deleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        string switchResource = _vault.GetVaultPath() + ".switch";
        var pathLock = PathLockRegistry.Get(switchResource);
        bool lockAcquired = await pathLock.WaitAsync(TimeSpan.FromMilliseconds(50));
        Assert.False(lockAcquired);

        resumeDelete.TrySetResult();
        var result = await resolveTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);

        Assert.True(await pathLock.WaitAsync(TimeSpan.FromSeconds(5)));
        pathLock.Release();
    }

    // 24. ResolveQuarantinedJournal_WhenRecordedState_PerformsCleanCleanup_WithoutPillarProofs
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenRecordedState_PerformsCleanCleanup_WithoutPillarProofs()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.RECORDED, _source!.Id, _target!.Id);

        // Break live identity and WinCred to prove RECORDED cleanup does NOT require pillar proofs
        _adapter.Identity = null;
        _credentials.Clear();

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.CleanCleanupCompleted, result.Status);
        Assert.Equal("CLEAN_RECORDED_REMOVED", result.ReasonCode);
        Assert.False(result.RestartRequired);
        Assert.False(File.Exists(_journal.JournalFilePath));
    }

    // 25. ResolveQuarantinedJournal_WhenPrecommitState_AndMetadataMatchesTarget_PerformsCleanCleanup
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenPrecommitState_AndMetadataMatchesTarget_PerformsCleanCleanup()
    {
        await SeedAccountsAsync();
        // Metadata is already target from SeedAccountsAsync
        await SeedJournalAsync(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id);

        // Break live identity to prove committed PRECOMMIT does not require live identity proof
        _adapter.Identity = null;

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.CleanCleanupCompleted, result.Status);
        Assert.Equal("CLEAN_COMMITTED_PRECOMMIT_REMOVED", result.ReasonCode);
        Assert.False(result.RestartRequired);
        Assert.False(File.Exists(_journal.JournalFilePath));
    }

    // 26. ResolveQuarantinedJournal_WhenPrecommitState_AndMetadataDoesNotMatchTarget_RequiresFullProof
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenPrecommitState_AndMetadataDoesNotMatchTarget_RequiresFullProof()
    {
        await SeedAccountsAsync();
        // Metadata active account is source, but target is PRECOMMIT target (uncommitted)
        await _accounts.SetActiveAccountIdAsync(_source!.Id);
        _credentials.Set(CreateCredential(_source.Email, "source-secret-payload"));
        _adapter.Identity = new AccountIdentityDto(_source.Email);
        await SeedJournalAsync(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        // Falls through to full proof on the active account (source). When proven coherent, restart is required.
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);
        Assert.Equal(_source.Id, result.CoherentAccountId);
        Assert.True(result.RestartRequired);
        Assert.False(File.Exists(_journal.JournalFilePath));
    }

    // 27. ResolveQuarantinedJournal_PreDeleteRace_EnrollmentStatusRevoked_AbortsDeletion
    [Fact]
    public async Task ResolveQuarantinedJournal_PreDeleteRace_EnrollmentStatusRevoked_AbortsDeletion()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        int accountReadCount = 0;
        _accounts.GetAccountBehavior = async (id, ct) =>
        {
            accountReadCount++;
            var acc = await _accounts.BaseGetAccountAsync(id, ct);
            if (accountReadCount >= 2 && acc != null)
            {
                // Drift to failed right before delete in final proof
                return acc with { ValidationStatus = AccountValidationStatus.Failed };
            }
            return acc;
        };

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("ACCOUNT_NOT_ENROLLED", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 28. ResolveQuarantinedJournal_PreDeleteRace_VaultSessionRemoved_AbortsDeletion
    [Fact]
    public async Task ResolveQuarantinedJournal_PreDeleteRace_VaultSessionRemoved_AbortsDeletion()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        int adapterCallCount = 0;
        _adapter.GetCurrentAccountBehavior = async ct =>
        {
            adapterCallCount++;
            if (adapterCallCount == 2)
            {
                // Remove vault session right before final proof checks it
                await _vault.RemoveSessionAsync(_target!.Id);
            }
            return new AccountIdentityDto(_target!.Email);
        };

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("VAULT_SESSION_MISSING", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 29. ResolveQuarantinedJournal_PreDeleteRace_WinCredChanged_AbortsDeletion
    [Fact]
    public async Task ResolveQuarantinedJournal_PreDeleteRace_WinCredChanged_AbortsDeletion()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        int winCredReadCount = 0;
        _credentials.ReadBehavior = ct =>
        {
            winCredReadCount++;
            if (winCredReadCount == 2)
            {
                // WinCred payload mutated right before delete in final proof
                return Task.FromResult<WinCredEntry?>(CreateCredential(_target!.Email, "altered-secret-payload"));
            }
            return Task.FromResult<WinCredEntry?>(CreateCredential(_target!.Email, "target-secret-payload"));
        };

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("CREDENTIAL_MISMATCH", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 30. ResolveQuarantinedJournal_PreDeleteRace_JournalTargetAccountIdChanged_AbortsDeletion
    [Fact]
    public async Task ResolveQuarantinedJournal_PreDeleteRace_JournalTargetAccountIdChanged_AbortsDeletion()
    {
        await SeedAccountsAsync();
        string txId = Guid.NewGuid().ToString("D");
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id, transactionId: txId);

        int adapterCallCount = 0;
        _adapter.GetCurrentAccountBehavior = async ct =>
        {
            adapterCallCount++;
            if (adapterCallCount == 2)
            {
                // Target account ID in journal changed on disk right before delete
                await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, "acc_third_party", transactionId: txId);
            }
            return new AccountIdentityDto(_target!.Email);
        };

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("CONCURRENT_MUTATION", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 31. ResolveQuarantinedJournal_PreDeleteRace_JournalSourceAccountIdChanged_AbortsDeletion
    [Fact]
    public async Task ResolveQuarantinedJournal_PreDeleteRace_JournalSourceAccountIdChanged_AbortsDeletion()
    {
        await SeedAccountsAsync();
        string txId = Guid.NewGuid().ToString("D");
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id, transactionId: txId);

        int adapterCallCount = 0;
        _adapter.GetCurrentAccountBehavior = async ct =>
        {
            adapterCallCount++;
            if (adapterCallCount == 2)
            {
                // Source account ID in journal changed on disk right before delete
                await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, "acc_alternate_source", _target!.Id, transactionId: txId);
            }
            return new AccountIdentityDto(_target!.Email);
        };

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, result.Status);
        Assert.Equal("CONCURRENT_MUTATION", result.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // 32. ResolveQuarantinedJournal_WhenDeleteIfUnchangedReturnsUnsupportedPlatform_ReturnsPersistenceFailureWithUnsupportedPlatformReasonCode (F-272-2 / F-274-2)
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenDeleteIfUnchangedReturnsUnsupportedPlatform_ReturnsPersistenceFailureWithUnsupportedPlatformReasonCode()
    {
        await SeedAccountsAsync();
        string txId = Guid.NewGuid().ToString("D");
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id, transactionId: txId);

        _journal.DeleteIfUnchangedBehavior = (entry, ct) =>
            Task.FromResult(SwitchJournalDeleteResult.UnsupportedPlatform("Conditional switch journal deletion is only supported on Windows."));

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.PersistenceFailure, result.Status);
        Assert.Equal("UNSUPPORTED_PLATFORM", result.ReasonCode);
        Assert.False(result.RestartRequired);
        Assert.Equal(_target!.Id, result.CoherentAccountId);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _process.LaunchCount);
        Assert.Equal(0, _process.QuiesceCount);
        Assert.Equal(0, _process.RestoreCount);
    }

    // 33. ResolveQuarantinedJournal_WhenRecordedState_AndDeleteReturnsUnsupportedPlatform_ReturnsPersistenceFailureWithRestartRequiredFalse
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenRecordedState_AndDeleteReturnsUnsupportedPlatform_ReturnsPersistenceFailureWithRestartRequiredFalse()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.RECORDED, _source!.Id, _target!.Id);

        _journal.DeleteIfUnchangedBehavior = (entry, ct) =>
            Task.FromResult(SwitchJournalDeleteResult.UnsupportedPlatform("Conditional switch journal deletion is only supported on Windows."));

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.PersistenceFailure, result.Status);
        Assert.Equal("UNSUPPORTED_PLATFORM", result.ReasonCode);
        Assert.False(result.RestartRequired);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(0, _credentials.WriteCount);
    }

    // 34. ResolveQuarantinedJournal_WhenCommittedPrecommit_AndDeleteReturnsUnsupportedPlatform_ReturnsPersistenceFailureWithRestartRequiredFalse
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenCommittedPrecommit_AndDeleteReturnsUnsupportedPlatform_ReturnsPersistenceFailureWithRestartRequiredFalse()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id);

        _journal.DeleteIfUnchangedBehavior = (entry, ct) =>
            Task.FromResult(SwitchJournalDeleteResult.UnsupportedPlatform("Conditional switch journal deletion is only supported on Windows."));

        var coordinator = CreateCoordinator();
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.PersistenceFailure, result.Status);
        Assert.Equal("UNSUPPORTED_PLATFORM", result.ReasonCode);
        Assert.False(result.RestartRequired);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(0, _credentials.WriteCount);
    }

    // 35. ReconcileStartupJournal_SetsJournalRecoveryStateToActionRequired_WhenJournalRetained
    [Fact]
    public async Task ReconcileStartupJournal_SetsJournalRecoveryStateToActionRequired_WhenJournalRetained()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);
        var status = coordinator.GetStatus();
        Assert.True(status.QuarantineActive);
        Assert.Equal(JournalRecoveryStates.ActionRequired, status.JournalRecoveryState);
    }

    // 36. ReconcileStartupJournal_SetsJournalRecoveryStateToNone_WhenClean
    [Fact]
    public async Task ReconcileStartupJournal_SetsJournalRecoveryStateToNone_WhenClean()
    {
        await SeedAccountsAsync();

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Clean, reconciliation.Status);
        var status = coordinator.GetStatus();
        Assert.False(status.QuarantineActive);
        Assert.Equal(JournalRecoveryStates.None, status.JournalRecoveryState);
    }

    [Fact]
    public async Task CleanCleanupRetry_AfterDeletionFailure_ReportsRestartAndPreservesQuarantine()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.RECORDED, _source!.Id, _target!.Id);
        var coordinator = CreateCoordinator();
        _journal.FailOnDelete = true;
        var failed = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.PersistenceFailure, failed.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(coordinator.GetStatus().QuarantineActive);
        _journal.FailOnDelete = false;
        var retry = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.CleanCleanupCompleted, retry.Status);
        Assert.True(retry.RestartRequired);
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.None, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(coordinator.GetStatus().QuarantineActive);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.True((await coordinator.SwitchAsync(_source.Id)).ManualRecoveryRequired);
        Assert.True((await coordinator.SwitchAutomaticallyAsync(_source.Id, _target.Id, () => true, "model-a", 30)).ManualRecoveryRequired);
        var absent = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.NoJournal, absent.Status);
        Assert.True(absent.RestartRequired);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Theory]
    [InlineData(SwitchJournalState.RECORDED, false)]
    [InlineData(SwitchJournalState.RECORDED, true)]
    [InlineData(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, false)]
    [InlineData(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, true)]
    public async Task RetainedCleanJournal_BlocksSwitchWithoutQuarantine(
        SwitchJournalState journalState, bool automatic)
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(journalState, _source!.Id, _target!.Id);
        _journal.FailOnDelete = true;

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Degraded, reconciliation.Status);
        Assert.False(coordinator.GetStatus().QuarantineActive);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);

        var result = automatic
            ? await coordinator.SwitchAutomaticallyAsync(_source.Id, _target.Id, () => true)
            : await coordinator.SwitchAsync(_source.Id);

        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    [Theory]
    [InlineData(JournalRecoveryStates.NotResolvable)]
    [InlineData(JournalRecoveryStates.Unknown)]
    [InlineData(JournalRecoveryStates.RestartRequired)]
    public async Task NonNoneJournalRecovery_BlocksManualAndAutomaticSwitches(string recoveryState)
    {
        await SeedAccountsAsync();
        if (recoveryState == JournalRecoveryStates.NotResolvable)
        {
            await File.WriteAllTextAsync(_journal.JournalFilePath, "corrupt journal", Encoding.UTF8);
        }
        else if (recoveryState == JournalRecoveryStates.Unknown)
        {
            _journal.ReadBehavior = _ => Task.FromResult(
                SwitchJournalReadResult.IoError(new IOException("Synthetic journal read failure.")));
        }
        else
        {
            await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);
        }

        var coordinator = CreateCoordinator();
        await coordinator.ReconcileStartupJournalAsync();
        if (recoveryState == JournalRecoveryStates.RestartRequired)
            await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(recoveryState, coordinator.GetStatus().JournalRecoveryState);
        var manual = await coordinator.SwitchAsync(_source!.Id);
        var automatic = await coordinator.SwitchAutomaticallyAsync(_source.Id, _target!.Id, () => true);

        Assert.False(manual.Success);
        Assert.True(manual.ManualRecoveryRequired);
        Assert.False(automatic.Success);
        Assert.True(automatic.ManualRecoveryRequired);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _accounts.FinalizeCalls);
    }

    // 37. ResolveQuarantinedJournal_TransitionsRecoveryStateToRestartRequired
    [Fact]
    public async Task ResolveQuarantinedJournal_TransitionsRecoveryStateToRestartRequired()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);

        var result = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);

        var status = coordinator.GetStatus();
        Assert.True(status.QuarantineActive);
        Assert.Equal(JournalRecoveryStates.RestartRequired, status.JournalRecoveryState);
    }

    // 38. NonJournalQuarantine_KeepsJournalRecoveryStateAsNone_WhileQuarantineActive
    [Fact]
    public async Task NonJournalQuarantine_KeepsJournalRecoveryStateAsNone_WhileQuarantineActive()
    {
        await SeedAccountsAsync();

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Clean, reconciliation.Status);
        Assert.False(coordinator.GetStatus().QuarantineActive);
        Assert.Equal(JournalRecoveryStates.None, coordinator.GetStatus().JournalRecoveryState);

        // Simulate non-journal quarantine (e.g. SessionVault mutation error)
        _vault.QuarantineUnresolvedMutation();

        var status = coordinator.GetStatus();
        Assert.True(status.QuarantineActive);
        // Non-journal quarantine MUST remain NONE and NOT collapse into RESTART_REQUIRED or ACTION_REQUIRED
        Assert.Equal(JournalRecoveryStates.None, status.JournalRecoveryState);
    }

    // 39. ReconcileStartupJournal_SetsJournalRecoveryStateToUnknown_WhenLockTimesOut
    [Fact]
    public async Task ReconcileStartupJournal_SetsJournalRecoveryStateToUnknown_WhenLockTimesOut()
    {
        await SeedAccountsAsync();
        string switchResource = _vault.GetVaultPath() + ".switch";
        var pathLock = PathLockRegistry.Get(switchResource);
        await pathLock.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var coordinator = CreateCoordinator(transactionTimeout: TimeSpan.FromMilliseconds(50));
            var reconciliation = await coordinator.ReconcileStartupJournalAsync();

            Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);
            var status = coordinator.GetStatus();
            Assert.True(status.QuarantineActive);
            Assert.Equal(JournalRecoveryStates.Unknown, status.JournalRecoveryState);
        }
        finally
        {
            pathLock.Release();
        }
    }

    // 40. StartupJournalReconciliation_SetsRecoveryStateToNotResolvable_WhenJournalIsCorrupt
    [Fact]
    public async Task StartupJournalReconciliation_SetsRecoveryStateToNotResolvable_WhenJournalIsCorrupt()
    {
        const string corruptBytes = "{\"magic\":\"AG2_SWJ1\",\"schemaVersion\":1,BROKEN_JSON";
        await File.WriteAllTextAsync(_journal.JournalFilePath, corruptBytes, Encoding.UTF8);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);
        var status = coordinator.GetStatus();
        Assert.True(status.QuarantineActive);
        Assert.Equal(JournalRecoveryStates.NotResolvable, status.JournalRecoveryState);
    }

    // 41. StartupJournalReconciliation_SetsRecoveryStateToNotResolvable_WhenJournalIsUnsupportedVersion
    [Fact]
    public async Task StartupJournalReconciliation_SetsRecoveryStateToNotResolvable_WhenJournalIsUnsupportedVersion()
    {
        string badVersionJson = JsonSerializer.Serialize(new
        {
            magic = SwitchJournalConstants.Magic,
            schemaVersion = 999,
            transactionId = "tx_future",
            state = "RECORDED"
        });
        await File.WriteAllTextAsync(_journal.JournalFilePath, badVersionJson, Encoding.UTF8);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);
        var status = coordinator.GetStatus();
        Assert.True(status.QuarantineActive);
        Assert.Equal(JournalRecoveryStates.NotResolvable, status.JournalRecoveryState);
    }

    // 42. ResolveQuarantinedJournal_RepeatedResolutionAfterRestartRequired_PreservesRestartRequired
    [Fact]
    public async Task ResolveQuarantinedJournal_RepeatedResolutionAfterRestartRequired_PreservesRestartRequired()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);

        var result1 = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result1.Status);
        Assert.Equal(JournalRecoveryStates.RestartRequired, coordinator.GetStatus().JournalRecoveryState);

        // Repeated resolve attempt when journal file is now absent
        var result2 = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.NoJournal, result2.Status);

        // Process-lifetime monotonicity: RESTART_REQUIRED must not revert to NONE
        var status = coordinator.GetStatus();
        Assert.True(status.QuarantineActive);
        Assert.Equal(JournalRecoveryStates.RestartRequired, status.JournalRecoveryState);
    }

    // 43. ObserveSwitchAsync_WhenRecoveryStateIsRestartRequired_PreservesRestartRequired
    [Fact]
    public async Task ObserveSwitchAsync_WhenRecoveryStateIsRestartRequired_PreservesRestartRequired()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);

        var resolveResult = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolveResult.Status);
        Assert.Equal(JournalRecoveryStates.RestartRequired, coordinator.GetStatus().JournalRecoveryState);

        // Subsequent switch attempt must fail fast with RecoveryUncertain and preserve RESTART_REQUIRED
        var switchResult = await coordinator.SwitchAsync(_source.Id);
        Assert.False(switchResult.Success);
        Assert.True(switchResult.ManualRecoveryRequired);

        var status = coordinator.GetStatus();
        Assert.True(status.QuarantineActive);
        Assert.Equal(JournalRecoveryStates.RestartRequired, status.JournalRecoveryState);
    }

    // 44. ResolveQuarantinedJournal_WhenLockTimesOut_PreservesRestartRequired
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenLockTimesOut_PreservesRestartRequired()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator(transactionTimeout: TimeSpan.FromMilliseconds(50));
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);

        var resolveResult = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolveResult.Status);
        Assert.Equal(JournalRecoveryStates.RestartRequired, coordinator.GetStatus().JournalRecoveryState);

        // Hold switch lock to induce timeout on second resolve
        string switchResource = _vault.GetVaultPath() + ".switch";
        var pathLock = PathLockRegistry.Get(switchResource);
        await pathLock.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var timeoutResult = await coordinator.ResolveQuarantinedJournalAsync();
            Assert.Equal(JournalResolutionStatus.PersistenceFailure, timeoutResult.Status);
            Assert.Equal("LOCK_TIMEOUT", timeoutResult.ReasonCode);

            var status = coordinator.GetStatus();
            Assert.True(status.QuarantineActive);
            // Must preserve RestartRequired across lock timeout
            Assert.Equal(JournalRecoveryStates.RestartRequired, status.JournalRecoveryState);
        }
        finally
        {
            pathLock.Release();
        }
    }

    // 45. ResolveQuarantinedJournal_WhenStateIsUnknown_LockTimeoutDoesNotRewriteToActionRequired
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenStateIsUnknown_LockTimeoutDoesNotRewriteToActionRequired()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        string switchResource = _vault.GetVaultPath() + ".switch";
        var pathLock = PathLockRegistry.Get(switchResource);
        await pathLock.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var coordinator = CreateCoordinator(transactionTimeout: TimeSpan.FromMilliseconds(50));
            var reconciliation = await coordinator.ReconcileStartupJournalAsync();
            Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);
            Assert.Equal(JournalRecoveryStates.Unknown, coordinator.GetStatus().JournalRecoveryState);

            var timeoutResult = await coordinator.ResolveQuarantinedJournalAsync();
            Assert.Equal(JournalResolutionStatus.PersistenceFailure, timeoutResult.Status);
            Assert.Equal("LOCK_TIMEOUT", timeoutResult.ReasonCode);

            var status = coordinator.GetStatus();
            Assert.True(status.QuarantineActive);
            // Must preserve Unknown, NOT rewrite to ActionRequired
            Assert.Equal(JournalRecoveryStates.Unknown, status.JournalRecoveryState);
        }
        finally
        {
            pathLock.Release();
        }
    }

    // 46. ResolveQuarantinedJournal_WhenActionRequired_FreshIoError_TransitionsToUnknown (F-286-2)
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenActionRequired_FreshIoError_TransitionsToUnknown()
    {
        await SeedAccountsAsync();
        // PRECOMMIT journal where metadata != target establishes ACTION_REQUIRED at startup
        await _accounts.SetActiveAccountIdAsync(_source!.Id);
        await SeedJournalAsync(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);

        // Fresh resolution encounters I/O uncertainty
        _journal.ReadBehavior = _ => Task.FromResult(SwitchJournalReadResult.IoError(new IOException("Disk read failed.")));

        var result = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.PersistenceFailure, result.Status);
        Assert.Equal("IO_ERROR", result.ReasonCode);

        var status = coordinator.GetStatus();
        Assert.True(status.QuarantineActive);
        Assert.Equal(JournalRecoveryStates.Unknown, status.JournalRecoveryState);
    }

    // 47. ResolveQuarantinedJournal_WhenRestartRequired_FreshIoError_PreservesRestartRequired (F-286-2)
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenRestartRequired_FreshIoError_PreservesRestartRequired()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);

        var resolve1 = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolve1.Status);
        Assert.Equal(JournalRecoveryStates.RestartRequired, coordinator.GetStatus().JournalRecoveryState);

        // Fresh resolution encounters I/O uncertainty
        _journal.ReadBehavior = _ => Task.FromResult(SwitchJournalReadResult.IoError(new IOException("Disk read failed.")));

        var resolve2 = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.PersistenceFailure, resolve2.Status);
        Assert.Equal("IO_ERROR", resolve2.ReasonCode);

        var status = coordinator.GetStatus();
        Assert.True(status.QuarantineActive);
        // RESTART_REQUIRED must supersede fresh IO_ERROR
        Assert.Equal(JournalRecoveryStates.RestartRequired, status.JournalRecoveryState);
    }

    // 48. ResolveQuarantinedJournal_WhenNotResolvable_FreshIoError_TransitionsToUnknown (F-286-2)
    [Fact]
    public async Task ResolveQuarantinedJournal_WhenNotResolvable_FreshIoError_TransitionsToUnknown()
    {
        await SeedAccountsAsync();
        const string corruptBytes = "{\"magic\":\"AG2_SWJ1\",\"schemaVersion\":1,BROKEN_JSON";
        await File.WriteAllTextAsync(_journal.JournalFilePath, corruptBytes, Encoding.UTF8);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);
        Assert.Equal(JournalRecoveryStates.NotResolvable, coordinator.GetStatus().JournalRecoveryState);

        // Fresh resolution encounters I/O uncertainty
        _journal.ReadBehavior = _ => Task.FromResult(SwitchJournalReadResult.IoError(new IOException("Disk read failed.")));

        var result = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.PersistenceFailure, result.Status);
        Assert.Equal("IO_ERROR", result.ReasonCode);

        var status = coordinator.GetStatus();
        Assert.True(status.QuarantineActive);
        Assert.Equal(JournalRecoveryStates.Unknown, status.JournalRecoveryState);
    }

    // Test doubles & spies

    private sealed class TestJournalStore : ISwitchJournalStore
    {
        private readonly SwitchJournalStore _underlying;
        private readonly List<string> _timeline;

        public List<SwitchJournalEntry> RecordedEntries { get; } = [];
        public List<string> Operations { get; } = [];
        public bool FailOnDelete { get; set; }
        public int DeleteCount { get; private set; }
        public int DeleteIfUnchangedCount { get; private set; }
        public Func<CancellationToken, Task<SwitchJournalReadResult>>? ReadBehavior { get; set; }
        public Func<CancellationToken, Task>? DeleteBehavior { get; set; }
        public Func<SwitchJournalEntry, CancellationToken, Task<SwitchJournalDeleteResult>>? DeleteIfUnchangedBehavior { get; set; }

        public string JournalFilePath => _underlying.JournalFilePath;

        public TestJournalStore(string journalFilePath, List<string> timeline)
        {
            _underlying = new SwitchJournalStore(journalFilePath);
            _timeline = timeline;
        }

        public async Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            _timeline.Add("JOURNAL_READ");
            Operations.Add("READ");
            if (ReadBehavior != null) return await ReadBehavior(cancellationToken);
            return await _underlying.ReadAsync(cancellationToken);
        }

        public async Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default)
        {
            _timeline.Add($"JOURNAL_WRITE:{entry.State}");
            Operations.Add($"WRITE:{entry.State}");
            RecordedEntries.Add(entry);
            await _underlying.WriteEntryAsync(entry, cancellationToken);
        }

        public async Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            _timeline.Add("JOURNAL_DELETE");
            Operations.Add("DELETE");
            DeleteCount++;

            if (DeleteBehavior != null)
            {
                await DeleteBehavior(cancellationToken);
            }

            if (FailOnDelete)
            {
                throw new IOException("Simulated journal delete failure");
            }

            await _underlying.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<SwitchJournalDeleteResult> DeleteIfUnchangedAsync(
            SwitchJournalEntry expectedEntry,
            CancellationToken cancellationToken = default)
        {
            _timeline.Add("JOURNAL_DELETE_IF_UNCHANGED");
            Operations.Add("DELETE_IF_UNCHANGED");
            DeleteIfUnchangedCount++;

            if (DeleteIfUnchangedBehavior != null)
            {
                return await DeleteIfUnchangedBehavior(expectedEntry, cancellationToken);
            }

            if (DeleteBehavior != null)
            {
                await DeleteBehavior(cancellationToken);
            }

            if (FailOnDelete)
            {
                return SwitchJournalDeleteResult.IoError(new IOException("Simulated journal delete failure"));
            }

            return await _underlying.DeleteIfUnchangedAsync(expectedEntry, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TestAccountStore : InMemoryAccountStore, IAccountStore
    {
        private readonly List<string>? _timeline;

        public bool FailFinalize { get; set; }
        public int FinalizeCalls { get; private set; }
        public Func<CancellationToken, Task<string?>>? GetActiveAccountIdBehavior { get; set; }
        public Func<string, CancellationToken, Task<AccountMetadata?>>? GetAccountBehavior { get; set; }

        public TestAccountStore(List<string>? timeline = null)
        {
            _timeline = timeline;
        }

        Task<AccountMetadata?> IAccountStore.GetAccountAsync(string id, CancellationToken cancellationToken)
        {
            if (GetAccountBehavior != null) return GetAccountBehavior(id, cancellationToken);
            return base.GetAccountAsync(id, cancellationToken);
        }

        public new Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default) =>
            ((IAccountStore)this).GetAccountAsync(id, cancellationToken);

        public Task<AccountMetadata?> BaseGetAccountAsync(string id, CancellationToken cancellationToken = default) =>
            base.GetAccountAsync(id, cancellationToken);

        Task<string?> IAccountStore.GetActiveAccountIdAsync(CancellationToken cancellationToken)
        {
            if (GetActiveAccountIdBehavior != null) return GetActiveAccountIdBehavior(cancellationToken);
            return base.GetActiveAccountIdAsync(cancellationToken);
        }

        public new Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default) =>
            ((IAccountStore)this).GetActiveAccountIdAsync(cancellationToken);

        Task<AccountMetadata?> IAccountStore.TryFinalizeSwitchAsync(
            string? expectedActiveAccountId,
            string targetAccountId,
            UpdateAccountInput targetUpdates,
            CancellationToken cancellationToken)
        {
            FinalizeCalls++;
            _timeline?.Add("METADATA_FINALIZE");
            if (FailFinalize)
            {
                return Task.FromResult<AccountMetadata?>(null);
            }
            return base.TryFinalizeSwitchAsync(expectedActiveAccountId, targetAccountId, targetUpdates, cancellationToken);
        }
    }

    private sealed class TestCredentialStore : IWinCredReader, IWinCredWriter
    {
        private readonly List<string> _timeline;
        private WinCredEntry? _current;
        public int WriteCount { get; private set; }
        public Func<CancellationToken, Task<WinCredEntry?>>? ReadBehavior { get; set; }

        public TestCredentialStore(List<string> timeline)
        {
            _timeline = timeline;
        }

        public void Set(WinCredEntry entry)
        {
            Clear();
            _current = Clone(entry);
        }

        public Task<WinCredEntry?> ReadCredentialAsync(string target = "gemini:antigravity", CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadBehavior != null) return ReadBehavior(cancellationToken);
            return Task.FromResult(_current == null ? null : Clone(_current));
        }

        public Task<bool> WriteCredentialAsync(WinCredEntry entry, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            _timeline.Add($"WINCRED_WRITE:{entry.UserName}");
            cancellationToken.ThrowIfCancellationRequested();
            Set(entry);
            return Task.FromResult(true);
        }

        public void Clear()
        {
            if (_current?.Blob is { Length: > 0 })
                CryptographicOperations.ZeroMemory(_current.Blob);
            _current = null;
        }

        private static WinCredEntry Clone(WinCredEntry entry) =>
            new(entry.Target, entry.Type, entry.UserName, entry.Persistence, (byte[])entry.Blob.Clone());
    }

    private sealed class TestAdapter : IAG2Adapter
    {
        private readonly List<string> _timeline;
        public AccountIdentityDto? Identity { get; set; }
        public Func<CancellationToken, Task<AccountIdentityDto?>>? GetCurrentAccountBehavior { get; set; }
        public int CallCount { get; private set; }

        public TestAdapter(List<string> timeline)
        {
            _timeline = timeline;
        }

        public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Ag2StatusDto(true, "HEALTHY", new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O")), "synthetic"));

        public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (GetCurrentAccountBehavior != null) return GetCurrentAccountBehavior(cancellationToken);
            return Task.FromResult(Identity);
        }

        public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<QuotaSnapshotDto?>(null);

        public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountQuotaObservation(Identity, null));

        public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O")));
    }

    private sealed class TestProcessLifecycle : IAG2ProcessLifecycle
    {
        private readonly List<string> _timeline;
        private readonly AG2ProcessSnapshot _snapshot = new(
            100, DateTime.UtcNow.AddMinutes(-1), @"C:\Synthetic\Antigravity\resources\bin\language_server.exe",
            ["--standalone", "--csrf_token", "synthetic"],
            ["--standalone", "--csrf_token", "[REDACTED]"], 10, DateTime.UtcNow.AddDays(-1),
            new string('A', 64), 1);
        private readonly AG2ProcessGeneration _replacement = new(
            101, DateTime.UtcNow, @"C:\Synthetic\Antigravity\resources\bin\language_server.exe", 2);

        public int StopCount { get; private set; }
        public int QuiesceCount { get; private set; }
        public int LaunchCount { get; private set; }
        public int RestoreCount { get; private set; }

        public TestProcessLifecycle(List<string> timeline)
        {
            _timeline = timeline;
        }

        public Task<AG2ProcessSnapshot> CaptureVerifiedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task RevalidateAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopVerifiedAsync(
            AG2ProcessSnapshot snapshot,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? verifyBeforeKillAsync = null,
            Action? onStopAttempted = null,
            Action? onStopIssued = null)
        {
            StopCount++;
            _timeline.Add("PROCESS_STOP");
            onStopIssued?.Invoke();
            return Task.CompletedTask;
        }

        public Task<AG2ProcessGeneration> LaunchAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            LaunchCount++;
            return Task.FromResult(_replacement);
        }

        public Task<AG2ProcessGeneration> WaitForHealthyReplacementAsync(
            AG2ProcessSnapshot original,
            AG2ProcessGeneration launched,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_replacement);

        public Task QuiesceForRollbackAsync(
            AG2ProcessSnapshot original,
            AG2ProcessGeneration? transactionOwnedReplacement,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            QuiesceCount++;
            _timeline.Add("PROCESS_QUIESCE_ROLLBACK");
            return Task.CompletedTask;
        }

        public Task<AG2ProcessGeneration> RestoreAsync(
            AG2ProcessSnapshot original,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            _timeline.Add("PROCESS_RESTORE");
            return Task.FromResult(new AG2ProcessGeneration(102, DateTime.UtcNow, original.ExecutablePath, 3));
        }

        public Task<bool> IsGenerationCurrentAsync(AG2ProcessGeneration generation, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
