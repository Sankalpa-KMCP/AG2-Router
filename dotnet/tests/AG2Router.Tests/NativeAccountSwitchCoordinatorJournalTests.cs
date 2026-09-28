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
            _journal,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: verificationTimeout ?? TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(500),
            transactionTimeout: transactionTimeout ?? TimeSpan.FromSeconds(5));

    private static WinCredEntry CreateCredential(string user, string secret) =>
        new("gemini:antigravity", 1, user, 2, Encoding.UTF8.GetBytes(secret));

    private async Task SeedJournalAsync(SwitchJournalState state, string sourceId, string targetId, string? reason = null)
    {
        var entry = new SwitchJournalEntry
        {
            Magic = SwitchJournalConstants.Magic,
            SchemaVersion = SwitchJournalConstants.CurrentSchemaVersion,
            TransactionId = Guid.NewGuid().ToString("D"),
            State = state,
            UpdatedAt = DateTimeOffset.UtcNow,
            SourceAccountId = sourceId,
            TargetAccountId = targetId,
            QuarantineReasonCode = reason
        };
        await _journal.WriteEntryAsync(entry);
    }

    private async Task WriteRawJournalAsync(string text)
    {
        await File.WriteAllTextAsync(_journal.JournalFilePath, text, Encoding.UTF8);
    }

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

    // =========================================================================
    // ADR-001 Step 3: Startup Switch Journal Reconciliation Tests (AC 1-30)
    // =========================================================================

    // 1. no journal -> Clean
    [Fact]
    public async Task Reconciliation_01_NoJournal_ReturnsClean()
    {
        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.Null(result.RetainedEntry);
        Assert.False(_vault.IsQuarantined);
    }

    // 2. RECORDED -> delete -> Clean
    [Fact]
    public async Task Reconciliation_02_Recorded_DeletesJournalAndReturnsClean()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.RECORDED, _source!.Id, _target!.Id);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.Null(result.RetainedEntry);
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.False(_vault.IsQuarantined);
    }

    // 3. RECORDED delete failure -> Degraded + retained + no quarantine
    [Fact]
    public async Task Reconciliation_03_Recorded_DeleteFailure_ReturnsDegradedAndRetains_NoQuarantine()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.RECORDED, _source!.Id, _target!.Id);
        _journal.FailOnDelete = true;

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Degraded, result.Status);
        Assert.NotNull(result.RetainedEntry);
        Assert.Equal(SwitchJournalState.RECORDED, result.RetainedEntry.State);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.False(_vault.IsQuarantined);
    }

    // 4. CREDENTIAL_APPLYING -> retain + quarantine
    [Fact]
    public async Task Reconciliation_04_CredentialApplying_RetainsAndQuarantines()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.NotNull(result.RetainedEntry);
        Assert.Equal(SwitchJournalState.CREDENTIAL_APPLYING, result.RetainedEntry.State);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.True(_vault.IsQuarantined);
    }

    // 5. PRECOMMIT + metadata target -> delete -> Clean
    [Fact]
    public async Task Reconciliation_05_Precommit_MetadataTarget_DeletesJournalAndReturnsClean()
    {
        await SeedAccountsAsync();
        await _accounts.SetActiveAccountIdAsync(_target!.Id);
        await SeedJournalAsync(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.Null(result.RetainedEntry);
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.False(_vault.IsQuarantined);
    }

    // 6. PRECOMMIT + metadata target delete failure -> Degraded + retained + no quarantine
    [Fact]
    public async Task Reconciliation_06_Precommit_MetadataTarget_DeleteFailure_ReturnsDegradedAndRetains_NoQuarantine()
    {
        await SeedAccountsAsync();
        await _accounts.SetActiveAccountIdAsync(_target!.Id);
        await SeedJournalAsync(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id);
        _journal.FailOnDelete = true;

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Degraded, result.Status);
        Assert.NotNull(result.RetainedEntry);
        Assert.Equal(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, result.RetainedEntry.State);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.False(_vault.IsQuarantined);
    }

    // 7. PRECOMMIT + metadata source -> quarantine + no metadata finalize
    [Fact]
    public async Task Reconciliation_07_Precommit_MetadataSource_QuarantinesAndDoesNotFinalize()
    {
        await SeedAccountsAsync();
        // Active account is _source.Id
        await SeedJournalAsync(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.NotNull(result.RetainedEntry);
        Assert.Equal(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, result.RetainedEntry.State);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.True(_vault.IsQuarantined);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    // 8. PRECOMMIT + metadata other -> quarantine
    [Fact]
    public async Task Reconciliation_08_Precommit_MetadataOther_Quarantines()
    {
        await SeedAccountsAsync();
        var other = await _accounts.AddAccountAsync(new CreateAccountInput(Email: "other@example.com"));
        await _accounts.SetActiveAccountIdAsync(other.Id);
        await SeedJournalAsync(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.NotNull(result.RetainedEntry);
        Assert.Equal(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, result.RetainedEntry.State);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.True(_vault.IsQuarantined);
    }

    // 9. PRECOMMIT + metadata null/missing -> quarantine
    [Fact]
    public async Task Reconciliation_09_Precommit_MetadataNullOrMissing_Quarantines()
    {
        await SeedAccountsAsync();
        _accounts.GetActiveAccountIdBehavior = _ => Task.FromResult<string?>(null);
        await SeedJournalAsync(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.NotNull(result.RetainedEntry);
        Assert.Equal(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, result.RetainedEntry.State);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.True(_vault.IsQuarantined);
    }

    // 10. PRECOMMIT + metadata read failure -> quarantine
    [Fact]
    public async Task Reconciliation_10_Precommit_MetadataReadFailure_Quarantines()
    {
        await SeedAccountsAsync();
        _accounts.GetActiveAccountIdBehavior = _ => Task.FromException<string?>(new IOException("Disk read failure"));
        await SeedJournalAsync(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.NotNull(result.RetainedEntry);
        Assert.Equal(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, result.RetainedEntry.State);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.True(_vault.IsQuarantined);
    }

    // 11. ROLLING_BACK -> quarantine
    [Fact]
    public async Task Reconciliation_11_RollingBack_Quarantines()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.ROLLING_BACK, _source!.Id, _target!.Id);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.NotNull(result.RetainedEntry);
        Assert.Equal(SwitchJournalState.ROLLING_BACK, result.RetainedEntry.State);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.True(_vault.IsQuarantined);
    }

    // 12. QUARANTINED -> re-mark quarantine
    [Fact]
    public async Task Reconciliation_12_Quarantined_ReMarksQuarantine()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.QUARANTINED, _source!.Id, _target!.Id, reason: "ROLLBACK_FAILED");

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.NotNull(result.RetainedEntry);
        Assert.Equal(SwitchJournalState.QUARANTINED, result.RetainedEntry.State);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.True(_vault.IsQuarantined);
    }

    // 13. malformed JSON -> bytes preserved + quarantine
    [Fact]
    public async Task Reconciliation_13_MalformedJson_PreservesBytesAndQuarantines()
    {
        const string malformed = "{\"magic\": \"AG2SWITCHJRNL\", \"state\": ";
        await WriteRawJournalAsync(malformed);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.True(_vault.IsQuarantined);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(malformed, await File.ReadAllTextAsync(_journal.JournalFilePath));
    }

    // 14. truncated JSON -> bytes preserved + quarantine
    [Fact]
    public async Task Reconciliation_14_TruncatedJson_PreservesBytesAndQuarantines()
    {
        const string truncated = "{\"magic\": \"AG2SWITCHJRNL\"";
        await WriteRawJournalAsync(truncated);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.True(_vault.IsQuarantined);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(truncated, await File.ReadAllTextAsync(_journal.JournalFilePath));
    }

    // 15. wrong magic -> artifact preserved + quarantine
    [Fact]
    public async Task Reconciliation_15_WrongMagic_PreservesArtifactAndQuarantines()
    {
        const string wrongMagic = "{\"magic\": \"WRONGMAGIC\", \"schemaVersion\": 1, \"transactionId\": \"123\", \"state\": \"RECORDED\"}";
        await WriteRawJournalAsync(wrongMagic);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.True(_vault.IsQuarantined);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(wrongMagic, await File.ReadAllTextAsync(_journal.JournalFilePath));
    }

    // 16. unsupported future schema -> artifact preserved + quarantine
    [Fact]
    public async Task Reconciliation_16_UnsupportedFutureSchema_PreservesArtifactAndQuarantines()
    {
        const string futureSchema = "{\"magic\": \"AG2SWITCHJRNL\", \"schemaVersion\": 999, \"transactionId\": \"123\", \"state\": \"RECORDED\"}";
        await WriteRawJournalAsync(futureSchema);

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.True(_vault.IsQuarantined);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(futureSchema, await File.ReadAllTextAsync(_journal.JournalFilePath));
    }

    // 17. journal read I/O error -> NOT clean/absent; quarantine
    [Fact]
    public async Task Reconciliation_17_JournalReadIoError_Quarantines()
    {
        _journal.ReadBehavior = _ => Task.FromResult(SwitchJournalReadResult.IoError(new IOException("Simulated file lock/disk I/O error")));

        var result = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.True(_vault.IsQuarantined);
    }

    // 18. path/cross-process lease timeout -> fail closed; no journal/metadata mutation
    [Fact]
    public async Task Reconciliation_18_PathOrCrossProcessLeaseTimeout_FailsClosedNoMutation()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.RECORDED, _source!.Id, _target!.Id);

        string switchResource = _vault.GetVaultPath() + ".switch";
        var existingLock = PathLockRegistry.Get(switchResource);
        Assert.True(await existingLock.WaitAsync(TimeSpan.FromSeconds(5)));
        try
        {
            var coordinator = CreateCoordinator(transactionTimeout: TimeSpan.FromMilliseconds(50));
            var result = await coordinator.ReconcileStartupJournalAsync();

            Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
            Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(_vault.IsQuarantined);
            // Must not delete journal or mutate metadata
            Assert.True(File.Exists(_journal.JournalFilePath));
            Assert.Equal(0, _accounts.FinalizeCalls);
        }
        finally
        {
            existingLock.Release();
        }
    }

    // 19. startup quarantine blocks switch
    [Fact]
    public async Task Reconciliation_19_StartupQuarantine_BlocksSwitch()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);

        var switchResult = await coordinator.SwitchAsync(_target!.Id);
        Assert.False(switchResult.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, switchResult.Code);
        Assert.True(switchResult.ManualRecoveryRequired);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
    }

    // 20. startup quarantine blocks enrollment
    [Fact]
    public async Task Reconciliation_20_StartupQuarantine_BlocksEnrollment()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);

        var enrollmentService = new AccountEnrollmentService(_adapter, _credentials, _vault, _accounts);
        var ex = await Assert.ThrowsAsync<AccountEnrollmentException>(() => enrollmentService.EnrollCurrentAccountAsync());
        Assert.Contains("unresolved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // 21. startup quarantine blocks removal
    [Fact]
    public async Task Reconciliation_21_StartupQuarantine_BlocksRemoval()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        var reconciliation = await coordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);

        var removalService = new AccountRemovalService(_accounts, _vault);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => removalService.RemoveAsync(_target!.Id));
        Assert.Contains("unresolved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // 22. reconciliation makes zero WinCred mutation calls
    [Fact]
    public async Task Reconciliation_22_MakesZeroWinCredMutationCalls()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(0, _credentials.WriteCount);
    }

    // 23. reconciliation makes zero process lifecycle mutation calls
    [Fact]
    public async Task Reconciliation_23_MakesZeroProcessLifecycleMutationCalls()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _process.QuiesceCount);
        Assert.Equal(0, _process.LaunchCount);
        Assert.Equal(0, _process.RestoreCount);
    }

    // 24. reconciliation never calls TryFinalizeSwitchAsync
    [Fact]
    public async Task Reconciliation_24_NeverCallsTryFinalizeSwitchAsync()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(0, _accounts.FinalizeCalls);
    }

    // 25. ordinary reconciliation makes zero live identity/RPC calls
    [Fact]
    public async Task Reconciliation_25_OrdinaryReconciliation_MakesZeroLiveIdentityOrRpcCalls()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.RECORDED, _source!.Id, _target!.Id);

        var coordinator = CreateCoordinator();
        await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(0, _adapter.CallCount);
    }

    // 26. coordinator requires a journal dependency at construction (ArgumentNullException when null)
    [Fact]
    public void Reconciliation_26_CoordinatorRequiresJournalDependencyAtConstruction_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new NativeAccountSwitchCoordinator(
            _accounts,
            _vault,
            _credentials,
            _credentials,
            _adapter,
            _process,
            switchJournalStore: null!));

        Assert.Equal("switchJournalStore", ex.ParamName);
    }

    // 27. hanging ReadAsync retains mutation ownership until settlement (use TCS fake)
    [Fact]
    public async Task Reconciliation_27_HangingReadAsync_RetainsMutationOwnershipUntilSettlement()
    {
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _journal.ReadBehavior = async ct =>
        {
            readEntered.TrySetResult();
            await resumeRead.Task;
            return SwitchJournalReadResult.Absent();
        };

        var coordinator = CreateCoordinator();
        var reconcileTask = coordinator.ReconcileStartupJournalAsync();
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        string switchResource = _vault.GetVaultPath() + ".switch";
        var pathLock = PathLockRegistry.Get(switchResource);
        bool lockAcquired = await pathLock.WaitAsync(TimeSpan.FromMilliseconds(50));
        Assert.False(lockAcquired);

        resumeRead.TrySetResult();
        var result = await reconcileTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StartupJournalReconciliationStatus.Clean, result.Status);

        Assert.True(await pathLock.WaitAsync(TimeSpan.FromSeconds(5)));
        pathLock.Release();
    }

    // 28. hanging DeleteAsync retains mutation ownership until settlement (use TCS fake)
    [Fact]
    public async Task Reconciliation_28_HangingDeleteAsync_RetainsMutationOwnershipUntilSettlement()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.RECORDED, _source!.Id, _target!.Id);
        var deleteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _journal.DeleteBehavior = async ct =>
        {
            deleteEntered.TrySetResult();
            await resumeDelete.Task;
        };

        var coordinator = CreateCoordinator();
        var reconcileTask = coordinator.ReconcileStartupJournalAsync();
        await deleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        string switchResource = _vault.GetVaultPath() + ".switch";
        var pathLock = PathLockRegistry.Get(switchResource);
        bool lockAcquired = await pathLock.WaitAsync(TimeSpan.FromMilliseconds(50));
        Assert.False(lockAcquired);

        resumeDelete.TrySetResult();
        var result = await reconcileTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StartupJournalReconciliationStatus.Clean, result.Status);

        Assert.True(await pathLock.WaitAsync(TimeSpan.FromSeconds(5)));
        pathLock.Release();
    }

    // 29. retained unresolved journal re-marks quarantine on repeated startup
    [Fact]
    public async Task Reconciliation_29_RetainedUnresolvedJournal_ReMarksQuarantineOnRepeatedStartup()
    {
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id);

        var coordinator1 = CreateCoordinator();
        var result1 = await coordinator1.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result1.Status);
        Assert.True(_vault.IsQuarantined);

        // Simulate subsequent startup by creating a new coordinator with the same underlying journal
        var coordinator2 = CreateCoordinator();
        var result2 = await coordinator2.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result2.Status);
        Assert.True(_vault.IsQuarantined);
        Assert.NotNull(result2.RetainedEntry);
        Assert.Equal(SwitchJournalState.CREDENTIAL_APPLYING, result2.RetainedEntry.State);
    }

    // 30. clean/degraded-cleanup path does not spuriously mark quarantine
    [Fact]
    public async Task Reconciliation_30_CleanOrDegradedCleanupPath_DoesNotSpuriouslyMarkQuarantine()
    {
        // 1. Clean path (no journal)
        var resultClean = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Clean, resultClean.Status);
        Assert.False(_vault.IsQuarantined);

        // 2. Degraded path (RECORDED delete failure)
        await SeedAccountsAsync();
        await SeedJournalAsync(SwitchJournalState.RECORDED, _source!.Id, _target!.Id);
        _journal.FailOnDelete = true;

        var resultDegraded = await CreateCoordinator().ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Degraded, resultDegraded.Status);
        Assert.False(_vault.IsQuarantined);
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
        public Func<CancellationToken, Task<SwitchJournalReadResult>>? ReadBehavior { get; set; }
        public Func<CancellationToken, Task>? DeleteBehavior { get; set; }

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
    }

    private sealed class TestAccountStore : InMemoryAccountStore, IAccountStore
    {
        private readonly List<string>? _timeline;

        public bool FailFinalize { get; set; }
        public int FinalizeCalls { get; private set; }
        public Func<CancellationToken, Task<string?>>? GetActiveAccountIdBehavior { get; set; }

        public TestAccountStore(List<string>? timeline = null)
        {
            _timeline = timeline;
        }

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
        public int CallCount { get; private set; }

        public TestAdapter(List<string> timeline)
        {
            _timeline = timeline;
        }

        public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) { CallCount++; return Task.FromResult(Status); }
        public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) { CallCount++; return Task.FromResult(Identity); }
        public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default) { CallCount++; return Task.FromResult<QuotaSnapshotDto?>(null); }
        public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default) { CallCount++; return Task.FromResult(new AccountQuotaObservation(Identity, null)); }
        public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default) { CallCount++; return Task.FromResult(Activity); }
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

        public Task<AG2ProcessGeneration> LaunchAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            LaunchCount++;
            return Task.FromResult(_replacement);
        }

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
