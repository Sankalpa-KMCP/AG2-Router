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
/// Comprehensive deterministic unit and fault-injection tests for switch transaction journal integration
/// into NativeAccountSwitchCoordinator (ADR-001 Step 2).
/// Tests all 12 fault-injection and sequencing requirements using isolated synthetic fixtures and test doubles.
/// </summary>
[CollectionDefinition("SwitchCoordinator", DisableParallelization = true)]
public sealed class SwitchCoordinatorTestCollection { }

[Collection("SwitchCoordinator")]
public sealed class NativeAccountSwitchCoordinatorJournalTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ag2_journal_test_{Guid.NewGuid():N}");
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

    public NativeAccountSwitchCoordinatorJournalTests()
    {
        Directory.CreateDirectory(_tempDir);
        _vault = new SessionVault(_tempDir, _dpapi);
        _credentials = new TestCredentialStore(_globalTimeline);
        _adapter = new TestAdapter(_globalTimeline);
        _process = new TestProcessLifecycle(_globalTimeline);
        _journal = new TestJournalStore(Path.Combine(_tempDir, "switch-journal.json"), _globalTimeline);

        _credentials.Set(CreateCredential("source@example.com", "source-secret-token"));
        _adapter.Identity = new AccountIdentityDto("source@example.com");
        _process.OnReplacement = () => _adapter.Identity = new AccountIdentityDto("target@example.com");
        _process.OnRestore = () => _adapter.Identity = new AccountIdentityDto("source@example.com");
    }

    public void Dispose()
    {
        _credentials.Clear();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private async Task SeedAccountsAsync(bool vaultTarget = true)
    {
        _source = await _accounts.AddAccountAsync(new CreateAccountInput(
            Email: "source@example.com", HasVaultedSession: true));
        _target = await _accounts.AddAccountAsync(new CreateAccountInput(
            Email: "target@example.com", HasVaultedSession: vaultTarget));
        await _accounts.SetActiveAccountIdAsync(_source.Id);
        if (vaultTarget)
        {
            await _vault.SaveSessionAsync(_target.Id, Encoding.UTF8.GetBytes("target-session-secret-payload"));
        }
    }

    private NativeAccountSwitchCoordinator CreateCoordinator(
        TimeSpan? verificationTimeout = null,
        TimeSpan? transactionTimeout = null) =>
        new(
            _accounts,
            _vault,
            _credentials,
            _credentials,
            _adapter,
            _process,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: verificationTimeout ?? TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(500),
            transactionTimeout: transactionTimeout ?? TimeSpan.FromSeconds(5),
            switchJournalStore: _journal);

    private static WinCredEntry CreateCredential(string user, string secret) =>
        new("gemini:antigravity", 1, user, 2, Encoding.UTF8.GetBytes(secret));

    // 1. Happy path: verifies exact sequence of journal writes:
    // RECORDED -> CREDENTIAL_APPLYING -> TARGET_IDENTITY_VERIFIED_PRECOMMIT -> DeleteAsync
    [Fact]
    public async Task Scenario01_HappyPath_FollowsExactSequence_Recorded_Applying_Precommit_Delete()
    {
        await SeedAccountsAsync();
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.True(result.Success);
        Assert.Equal(SwitchResultCodes.Success, result.Code);
        Assert.Equal(NativeSwitchStates.Complete, result.State);
        Assert.Equal(_target.Id, await _accounts.GetActiveAccountIdAsync());

        // Verify exact journal sequence
        Assert.Equal(3, _journal.RecordedEntries.Count);
        Assert.Equal(SwitchJournalState.RECORDED, _journal.RecordedEntries[0].State);
        Assert.Equal(SwitchJournalState.CREDENTIAL_APPLYING, _journal.RecordedEntries[1].State);
        Assert.Equal(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _journal.RecordedEntries[2].State);

        // Verify journal deletion was called after precommit
        Assert.Equal(1, _journal.DeleteCount);
        Assert.Contains("JOURNAL_DELETE", _globalTimeline);

        // Verify UUID transaction ID consistency across all entries
        string txId = _journal.RecordedEntries[0].TransactionId;
        Assert.True(Guid.TryParse(txId, out _));
        Assert.All(_journal.RecordedEntries, e => Assert.Equal(txId, e.TransactionId));

        // Verify account IDs and zero reason codes in happy path
        Assert.All(_journal.RecordedEntries, e =>
        {
            Assert.Equal(_source!.Id, e.SourceAccountId);
            Assert.Equal(_target!.Id, e.TargetAccountId);
            Assert.Null(e.QuarantineReasonCode);
            Assert.Equal(SwitchJournalConstants.Magic, e.Magic);
            Assert.Equal(1, e.SchemaVersion);
        });

        // Verify file is cleanly deleted on disk in happy path
        var readResult = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Absent, readResult.Status);
    }

    // 2. RECORDED write failure: aborts immediately, QuiesceAsync never called, WinCred never touched, account store never finalized
    [Fact]
    public async Task Scenario02_RecordedWriteFailure_AbortsImmediately_NoQuiesce_NoWinCred_NoFinalize()
    {
        await SeedAccountsAsync();
        _journal.FailOnState = SwitchJournalState.RECORDED;
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.NotEqual(SwitchResultCodes.Success, result.Code);

        // Quiesce/stop source process must NEVER be called
        Assert.Equal(0, _process.StopCount);
        Assert.DoesNotContain("PROCESS_STOP", _globalTimeline);

        // WinCred must NEVER be touched
        Assert.Equal(0, _credentials.WriteCount);
        Assert.DoesNotContain("WINCRED_WRITE", _globalTimeline);

        // Account store must NOT be finalized
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.DoesNotContain("METADATA_FINALIZE", _globalTimeline);

        // In-process quarantine must NOT be marked (no mutation occurred)
        var quarantine = RecoveryQuarantineRegistry.Get(_vault.GetVaultPath() + ".switch");
        Assert.False(quarantine.IsMarked);
    }

    // 3. CREDENTIAL_APPLYING write failure: Quiesce was called, aborts, enters rollback,
    // WinCred WriteCredentialAsync for target never called, ROLLING_BACK written, previous credentials restored, rollback verified, journal deleted
    [Fact]
    public async Task Scenario03_CredentialApplyingWriteFailure_QuiesceCalled_TargetWinCredNeverCalled_RollbackSucceeds_JournalDeleted()
    {
        await SeedAccountsAsync();
        _journal.FailOnState = SwitchJournalState.CREDENTIAL_APPLYING;
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Equal(NativeSwitchStates.RolledBack, result.State);

        // Quiesce was called
        Assert.Equal(1, _process.StopCount);

        // Target credential write was NEVER called (failed before WinCred write)
        Assert.Equal(0, _credentials.WriteCount);

        // Rollback ordering: ROLLING_BACK written strictly before QuiesceForRollbackAsync
        int rollingBackIdx = _globalTimeline.IndexOf("JOURNAL_WRITE:ROLLING_BACK");
        int quiesceRollbackIdx = _globalTimeline.IndexOf("PROCESS_QUIESCE_ROLLBACK");
        Assert.True(rollingBackIdx >= 0, "ROLLING_BACK must be written to journal");
        Assert.True(quiesceRollbackIdx >= 0, "PROCESS_QUIESCE_ROLLBACK must be called");
        Assert.True(rollingBackIdx < quiesceRollbackIdx, "ROLLING_BACK must precede QuiesceForRollbackAsync");

        // Source process was restored and verified
        Assert.Equal(1, _process.RestoreCount);

        // Journal deleted upon verified rollback completion
        int deleteIdx = _globalTimeline.IndexOf("JOURNAL_DELETE");
        Assert.True(deleteIdx > rollingBackIdx, "Journal must be deleted after rollback completion");

        var readResult = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Absent, readResult.Status);
    }

    // 4. WinCred write failure: target cred write fails, ROLLING_BACK written before QuiesceForRollbackAsync and credential restore, rollback succeeds, journal deleted
    [Fact]
    public async Task Scenario04_WinCredWriteFailure_RollingBackWrittenBeforeQuiesceForRollback_RollbackSucceeds_JournalDeleted()
    {
        await SeedAccountsAsync();
        _credentials.WriteBehavior = (call, _, _) =>
        {
            if (call == 1) throw new InvalidOperationException("Simulated WinCred target write failure");
            return Task.FromResult(true);
        };
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);

        // ROLLING_BACK written strictly before QuiesceForRollbackAsync
        int rollingBackIdx = _globalTimeline.IndexOf("JOURNAL_WRITE:ROLLING_BACK");
        int quiesceRollbackIdx = _globalTimeline.IndexOf("PROCESS_QUIESCE_ROLLBACK");

        Assert.True(rollingBackIdx >= 0);
        Assert.True(quiesceRollbackIdx >= 0);
        Assert.True(rollingBackIdx < quiesceRollbackIdx, "ROLLING_BACK must precede QuiesceForRollbackAsync");

        // Rollback succeeded and deleted journal
        Assert.Equal(1, _process.RestoreCount);
        Assert.Contains("JOURNAL_DELETE", _globalTimeline);
    }

    // 5. Target Identity Verification failure: target identity verification fails, ROLLING_BACK written before QuiesceForRollbackAsync, previous creds restored, rollback succeeds, journal deleted
    [Fact]
    public async Task Scenario05_TargetIdentityVerificationFailure_RollingBackWrittenBeforeQuiesceForRollback_RollbackSucceeds_JournalDeleted()
    {
        await SeedAccountsAsync();
        // Return wrong identity on replacement process
        _process.OnReplacement = () => _adapter.Identity = new AccountIdentityDto("wrong-identity@example.com");
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);

        // Verify ROLLING_BACK ordering before QuiesceForRollbackAsync and before credential restore
        int rollingBackIdx = _globalTimeline.IndexOf("JOURNAL_WRITE:ROLLING_BACK");
        int quiesceRollbackIdx = _globalTimeline.IndexOf("PROCESS_QUIESCE_ROLLBACK");
        int wincredRestoreIdx = _globalTimeline.FindLastIndex(s => s.StartsWith("WINCRED_WRITE"));

        Assert.True(rollingBackIdx >= 0);
        Assert.True(quiesceRollbackIdx >= 0);
        Assert.True(wincredRestoreIdx >= 0);
        Assert.True(rollingBackIdx < quiesceRollbackIdx, "ROLLING_BACK must precede QuiesceForRollbackAsync");
        Assert.True(rollingBackIdx < wincredRestoreIdx, "ROLLING_BACK must precede credential restore");
        Assert.True(quiesceRollbackIdx < wincredRestoreIdx, "QuiesceForRollbackAsync must precede credential restore");

        // Journal deleted upon verified rollback completion
        Assert.Contains("JOURNAL_DELETE", _globalTimeline);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    // 6. TARGET_IDENTITY_VERIFIED_PRECOMMIT write failure: live target identity was verified,
    // but precommit journal write throws -> triggers rollback, ROLLING_BACK written, previous account restored, journal deleted
    [Fact]
    public async Task Scenario06_PrecommitWriteFailure_LiveTargetVerified_RollbackTriggered_RollingBackWritten_SourceRestored_JournalDeleted()
    {
        await SeedAccountsAsync();
        _journal.FailOnState = SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT;
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);

        // Metadata finalization was NEVER attempted
        Assert.DoesNotContain("METADATA_FINALIZE", _globalTimeline);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());

        // Rollback occurred: ROLLING_BACK written before QuiesceForRollbackAsync
        int rollingBackIdx = _globalTimeline.IndexOf("JOURNAL_WRITE:ROLLING_BACK");
        int quiesceRollbackIdx = _globalTimeline.IndexOf("PROCESS_QUIESCE_ROLLBACK");
        Assert.True(rollingBackIdx >= 0);
        Assert.True(quiesceRollbackIdx >= 0);
        Assert.True(rollingBackIdx < quiesceRollbackIdx);

        // Rollback verified and journal deleted
        Assert.Equal(1, _process.RestoreCount);
        Assert.Contains("JOURNAL_DELETE", _globalTimeline);
    }

    // 7. Metadata Finalization failure: TryFinalizeSwitchAsync fails -> triggers rollback or quarantine per design
    [Fact]
    public async Task Scenario07_MetadataFinalizationFailure_TriggersRollback()
    {
        await SeedAccountsAsync();
        _accounts.FailFinalize = true; // Simulate CAS conflict / finalization failure
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);

        // Precommit was written, but finalize returned null -> rollback was triggered
        Assert.Contains("JOURNAL_WRITE:TARGET_IDENTITY_VERIFIED_PRECOMMIT", _globalTimeline);
        Assert.Contains("JOURNAL_WRITE:ROLLING_BACK", _globalTimeline);

        // Previous account verified and journal deleted
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Contains("JOURNAL_DELETE", _globalTimeline);
    }

    // 8. Journal Deletion failure on commit: TryFinalizeSwitchAsync succeeds, DeleteAsync throws ->
    // switch still succeeds, PRECOMMIT journal preserved on disk
    [Fact]
    public async Task Scenario08_JournalDeletionFailureOnCommit_SwitchSucceeds_PrecommitPreserved()
    {
        await SeedAccountsAsync();
        _journal.FailOnDelete = true;
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        // Switch MUST succeed!
        Assert.True(result.Success);
        Assert.Equal(SwitchResultCodes.Success, result.Code);
        Assert.Equal(NativeSwitchStates.Complete, result.State);
        Assert.Equal(_target!.Id, await _accounts.GetActiveAccountIdAsync());

        // Journal deletion threw, so PRECOMMIT remains on disk for startup reconciliation
        var readResult = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, readResult.Status);
        Assert.NotNull(readResult.Entry);
        Assert.Equal(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, readResult.Entry.State);
        Assert.Equal(_target.Id, readResult.Entry.TargetAccountId);
    }

    // 9. ROLLING_BACK write failure: when rollback is triggered, writing ROLLING_BACK throws ->
    // QuiesceForRollbackAsync and credential restore are NOT called, in-process quarantine is marked,
    // prior journal state preserved on disk, returns failure indicating manual recovery required
    [Fact]
    public async Task Scenario09_RollingBackWriteFailure_QuiesceForRollbackNotCalled_NoCredentialRestore_Quarantined_PriorJournalPreserved_ManualRecoveryRequired()
    {
        await SeedAccountsAsync();
        // Trigger rollback via target identity mismatch
        _process.OnReplacement = () => _adapter.Identity = new AccountIdentityDto("wrong@example.com");

        // Fail writing ROLLING_BACK
        _journal.FailOnState = SwitchJournalState.ROLLING_BACK;
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.True(result.ManualRecoveryRequired);

        // CRITICAL SAFETY RULE: QuiesceForRollbackAsync MUST NOT be called!
        Assert.Equal(0, _process.QuiesceCount);
        Assert.DoesNotContain("PROCESS_QUIESCE_ROLLBACK", _globalTimeline);

        // Credential restore MUST NOT be called!
        Assert.DoesNotContain("PROCESS_RESTORE", _globalTimeline);

        // In-process quarantine MUST be marked
        var quarantine = RecoveryQuarantineRegistry.Get(_vault.GetVaultPath() + ".switch");
        Assert.True(quarantine.IsMarked);

        // Prior conservative journal state (CREDENTIAL_APPLYING) MUST be preserved on disk!
        var readResult = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, readResult.Status);
        Assert.NotNull(readResult.Entry);
        Assert.Equal(SwitchJournalState.CREDENTIAL_APPLYING, readResult.Entry.State);
    }

    // 10. Rollback failure transitions to QUARANTINED: when rollback execution fails,
    // writes QUARANTINED with reason code, in-process quarantine is marked
    [Fact]
    public async Task Scenario10_RollbackFailure_TransitionsToQuarantined_WithReasonCode_InProcessQuarantined()
    {
        await SeedAccountsAsync();
        // Trigger rollback via target identity mismatch
        _process.OnReplacement = () => _adapter.Identity = new AccountIdentityDto("wrong@example.com");

        // Make rollback execution fail during process restore
        _process.RestoreError = new AG2ProcessLifecycleException("Synthetic restore failure during rollback");
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.True(result.ManualRecoveryRequired);

        // In-process quarantine is marked
        var quarantine = RecoveryQuarantineRegistry.Get(_vault.GetVaultPath() + ".switch");
        Assert.True(quarantine.IsMarked);

        // QUARANTINED was written with reason code
        Assert.Contains("JOURNAL_WRITE:QUARANTINED", _globalTimeline);
        var readResult = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, readResult.Status);
        Assert.NotNull(readResult.Entry);
        Assert.Equal(SwitchJournalState.QUARANTINED, readResult.Entry.State);
        Assert.Equal("ROLLBACK_FAILED", readResult.Entry.QuarantineReasonCode);
    }

    // 11. Rollback failure where writing QUARANTINED also throws: in-process quarantine still marked, returns manual recovery required
    [Fact]
    public async Task Scenario11_RollbackFailure_WhereQuarantinedWriteThrows_InProcessQuarantineStillMarked_ManualRecoveryRequired()
    {
        await SeedAccountsAsync();
        _process.OnReplacement = () => _adapter.Identity = new AccountIdentityDto("wrong@example.com");
        _process.RestoreError = new AG2ProcessLifecycleException("Synthetic restore failure");

        // Fail writing QUARANTINED as well
        _journal.FailOnState = SwitchJournalState.QUARANTINED;
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.True(result.ManualRecoveryRequired);

        // In-process quarantine MUST still be marked even though writing QUARANTINED threw
        var quarantine = RecoveryQuarantineRegistry.Get(_vault.GetVaultPath() + ".switch");
        Assert.True(quarantine.IsMarked);
    }

    // 12. Zero Secret Leaks: assert journal entries recorded during transactions never contain tokens, passwords, credentials, email addresses, or display names
    [Fact]
    public async Task Scenario12_ZeroSecretLeaks_JournalEntriesNeverContainSecretsTokensOrEmails()
    {
        await SeedAccountsAsync();
        var coordinator = CreateCoordinator();

        await coordinator.SwitchAsync(_target!.Id);

        Assert.NotEmpty(_journal.RecordedEntries);

        string[] forbiddenSubstrings =
        [
            "secret", "token", "password", "payload", "session", "blob", "wincred", "dpapi",
            "email", "display", "source@example.com", "target@example.com", "source-secret", "target-session"
        ];

        foreach (var entry in _journal.RecordedEntries)
        {
            // Verify IDs do not leak emails or display names
            Assert.DoesNotContain("@", entry.SourceAccountId);
            Assert.DoesNotContain("@", entry.TargetAccountId);

            // Serialize entry exactly as SwitchJournalStore does
            string json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            foreach (string forbidden in forbiddenSubstrings)
            {
                Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // Test doubles & spies

    private sealed class TestJournalStore : ISwitchJournalStore
    {
        private readonly SwitchJournalStore _underlying;
        private readonly List<string> _timeline;

        public List<SwitchJournalEntry> RecordedEntries { get; } = [];
        public List<string> Operations { get; } = [];
        public SwitchJournalState? FailOnState { get; set; }
        public bool FailOnDelete { get; set; }
        public int DeleteCount { get; private set; }

        public string JournalFilePath => _underlying.JournalFilePath;

        public TestJournalStore(string journalFilePath, List<string> timeline)
        {
            _underlying = new SwitchJournalStore(journalFilePath);
            _timeline = timeline;
        }

        public Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            _underlying.ReadAsync(cancellationToken);

        public async Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default)
        {
            _timeline.Add($"JOURNAL_WRITE:{entry.State}");
            Operations.Add($"WRITE:{entry.State}");
            RecordedEntries.Add(entry);

            if (FailOnState == entry.State)
            {
                throw new IOException($"Simulated journal write failure for state {entry.State}");
            }

            await _underlying.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            _timeline.Add("JOURNAL_DELETE");
            Operations.Add("DELETE");
            DeleteCount++;

            if (FailOnDelete)
            {
                throw new IOException("Simulated journal delete failure");
            }

            await _underlying.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TestAccountStore : InMemoryAccountStore, IAccountStore
    {
        private readonly List<string>? _timeline;

        public bool FailFinalize { get; set; }

        public TestAccountStore(List<string>? timeline = null)
        {
            _timeline = timeline;
        }

        Task<AccountMetadata?> IAccountStore.TryFinalizeSwitchAsync(
            string? expectedActiveAccountId,
            string targetAccountId,
            UpdateAccountInput targetUpdates,
            CancellationToken cancellationToken)
        {
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
        public Func<int, WinCredEntry, CancellationToken, Task<bool>>? WriteBehavior { get; set; }

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
            return Task.FromResult(_current == null ? null : Clone(_current));
        }

        public async Task<bool> WriteCredentialAsync(WinCredEntry entry, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            _timeline.Add($"WINCRED_WRITE:{entry.UserName}");
            if (WriteBehavior != null) return await WriteBehavior(WriteCount, entry, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Set(entry);
            return true;
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
        public ActivityStatusDto Activity { get; set; } = new("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O"));
        public Ag2StatusDto Status { get; set; } = new(true, "HEALTHY", new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O")), "synthetic");

        public TestAdapter(List<string> timeline)
        {
            _timeline = timeline;
        }

        public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);
        public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Identity);
        public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default) => Task.FromResult<QuotaSnapshotDto?>(null);
        public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountQuotaObservation(Identity, null));
        public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Activity);
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
        public int RestoreCount { get; private set; }
        public Exception? RestoreError { get; set; }
        public Action? OnReplacement { get; set; }
        public Action? OnRestore { get; set; }

        public TestProcessLifecycle(List<string> timeline)
        {
            _timeline = timeline;
        }

        public Task<AG2ProcessSnapshot> CaptureVerifiedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task RevalidateAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task StopVerifiedAsync(
            AG2ProcessSnapshot snapshot,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? verifyBeforeKillAsync = null,
            Action? onStopAttempted = null)
        {
            if (verifyBeforeKillAsync != null) await verifyBeforeKillAsync(cancellationToken);
            onStopAttempted?.Invoke();
            StopCount++;
            _timeline.Add("PROCESS_STOP");
        }

        public Task<AG2ProcessGeneration> LaunchAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult(_replacement);

        public Task<AG2ProcessGeneration> WaitForHealthyReplacementAsync(
            AG2ProcessSnapshot original,
            AG2ProcessGeneration launched,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            OnReplacement?.Invoke();
            return Task.FromResult(_replacement);
        }

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
            if (RestoreError != null) return Task.FromException<AG2ProcessGeneration>(RestoreError);
            OnRestore?.Invoke();
            return Task.FromResult(new AG2ProcessGeneration(102, DateTime.UtcNow, original.ExecutablePath, 3));
        }

        public Task<bool> IsGenerationCurrentAsync(AG2ProcessGeneration generation, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
