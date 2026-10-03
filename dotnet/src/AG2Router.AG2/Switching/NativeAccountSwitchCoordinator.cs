using System.Globalization;
using System.Security.Cryptography;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.AG2.Security;
using AG2Router.AG2.Vault;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Switching;

public sealed class NativeAccountSwitchCoordinator : INativeAccountSwitchCoordinator
{
    private const string WinCredTarget = "gemini:antigravity";
    private const int MaxCredentialBlobBytes = 2560;
    private static readonly SemaphoreSlim SwitchGate = new(1, 1);

    private readonly IAccountStore _accountStore;
    private readonly SessionVault _sessionVault;
    private readonly IWinCredReader _winCredReader;
    private readonly IWinCredWriter _winCredWriter;
    private readonly IAG2Adapter _adapter;
    private readonly IAG2ProcessLifecycle _processLifecycle;
    private readonly TimeSpan _processTimeout;
    private readonly TimeSpan _verificationTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _rollbackTimeout;
    private readonly TimeSpan _transactionTimeout;
    private readonly IQuotaObservationStore? _quotaObservationStore;
    private readonly TimeProvider _timeProvider;
    private readonly RecoveryQuarantine _recoveryQuarantine;
    private readonly ISwitchJournalStore _journalStore;
    private readonly object _statusLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private volatile bool _isShuttingDown;
    internal Func<Task>? BeforeLeaseReleaseAsync { get; set; }
    // Internal fault-injection seam; production always acquires the real file lease.
    internal Func<string, CancellationToken, Task<IAsyncDisposable>> AcquireRecoveryLeaseAsync { get; set; } =
        async (resource, token) => await CrossProcessFileLease.AcquireAsync(resource, token).ConfigureAwait(false);
    internal void SetJournalRecoveryStateForTest(string state) { lock (_statusLock) { _journalRecoveryState = state; } }
    internal void SetActiveTransactionForTest(string? txId, string state = NativeSwitchStates.Idle) { lock (_statusLock) { _activeTransactionId = txId; _currentState = state; } }
    internal static SemaphoreSlim SwitchGateForTest => SwitchGate;
    internal void SetShuttingDownForTest(bool isShuttingDown) { lock (_statusLock) { _isShuttingDown = isShuttingDown; } }

    private string _currentState = NativeSwitchStates.Idle;
    private string? _activeTransactionId;
    private NativeSwitchResult? _lastResult;
    private string _journalRecoveryState = JournalRecoveryStates.None;

    public NativeAccountSwitchCoordinator(
        IAccountStore accountStore,
        SessionVault sessionVault,
        IWinCredReader winCredReader,
        IWinCredWriter winCredWriter,
        IAG2Adapter adapter,
        IAG2ProcessLifecycle processLifecycle,
        ISwitchJournalStore switchJournalStore,
        TimeSpan? processTimeout = null,
        TimeSpan? verificationTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? rollbackTimeout = null,
        TimeSpan? transactionTimeout = null,
        IQuotaObservationStore? quotaObservationStore = null,
        TimeProvider? timeProvider = null)
    {
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        _sessionVault = sessionVault ?? throw new ArgumentNullException(nameof(sessionVault));
        _winCredReader = winCredReader ?? throw new ArgumentNullException(nameof(winCredReader));
        _winCredWriter = winCredWriter ?? throw new ArgumentNullException(nameof(winCredWriter));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _processLifecycle = processLifecycle ?? throw new ArgumentNullException(nameof(processLifecycle));
        _journalStore = switchJournalStore ?? throw new ArgumentNullException(nameof(switchJournalStore));
        _processTimeout = processTimeout ?? TimeSpan.FromSeconds(10);
        _verificationTimeout = verificationTimeout ?? TimeSpan.FromSeconds(20);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        _rollbackTimeout = rollbackTimeout ?? (_processTimeout + _verificationTimeout + TimeSpan.FromSeconds(10));
        if (_rollbackTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(rollbackTimeout));
        _transactionTimeout = transactionTimeout ??
            (_processTimeout + _verificationTimeout + _rollbackTimeout + TimeSpan.FromSeconds(30));
        if (_transactionTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(transactionTimeout));
        _quotaObservationStore = quotaObservationStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _recoveryQuarantine = RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath() + ".switch");
    }

    public NativeSwitchStatus GetStatus()
    {
        lock (_statusLock)
        {
            if (_recoveryQuarantine.IsMarked)
            {
                string now = DateTimeOffset.UtcNow.ToString("O");
                var uncertain = _lastResult?.ManualRecoveryRequired == true
                    ? _lastResult
                    : new NativeSwitchResult(_activeTransactionId, false,
                        SwitchResultCodes.SwitchFailedRollbackFailed, NativeSwitchStates.Failed,
                        string.Empty, null, null, null,
                        "Switch outcome is unresolved; manual recovery is required.",
                        [], now, now, ManualRecoveryRequired: true);
                return new NativeSwitchStatus(
                    _activeTransactionId,
                    NativeSwitchStates.Failed,
                    uncertain,
                    QuarantineActive: true,
                    JournalRecoveryState: _journalRecoveryState);
            }
            return new NativeSwitchStatus(
                _activeTransactionId,
                _currentState,
                _lastResult,
                QuarantineActive: false,
                JournalRecoveryState: _journalRecoveryState);
        }
    }

    public async Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_statusLock) { _isShuttingDown = true; }
        _shutdownCts.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await SwitchGate.WaitAsync(cts.Token).ConfigureAwait(false);
            SwitchGate.Release();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("An account switch is still completing or recovering; shutdown was not completed.");
        }
    }

    private StartupJournalReconciliationResult RecordStartupReconciliationResult(
        StartupJournalReconciliationResult result, string recoveryState)
    {
        lock (_statusLock)
        {
            if (_journalRecoveryState != JournalRecoveryStates.RestartRequired)
            {
                _journalRecoveryState = recoveryState;
            }
        }
        return result;
    }

    public async Task<StartupJournalReconciliationResult> ReconcileStartupJournalAsync(CancellationToken cancellationToken = default)
    {
        await SwitchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string switchResource = _sessionVault.GetVaultPath() + ".switch";
            var switchLock = PathLockRegistry.Get(switchResource);
            if (!await switchLock.WaitAsync(_transactionTimeout, cancellationToken).ConfigureAwait(false))
            {
                _sessionVault.QuarantineUnresolvedMutation();
                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, "Lock acquisition timed out during startup reconciliation."), JournalRecoveryStates.Unknown);
            }

            IAsyncDisposable? switchLease = null;
            async Task<StartupJournalReconciliationResult> ReconcileUnderLeaseAsync()
            {
                try
                {
                    switchLease = await AcquireRecoveryLeaseAsync(switchResource, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Cross-process lease acquisition failed: {ex.Message}"), JournalRecoveryStates.Unknown);
                }

                SwitchJournalReadResult readResult;
                try
                {
                    readResult = await _journalStore.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"I/O error reading switch journal: {ex.Message}"), JournalRecoveryStates.Unknown);
                }

                if (readResult.Status == SwitchJournalReadStatus.Absent)
                {
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Clean, "No switch journal present."), JournalRecoveryStates.None);
                }

                if (readResult.Status == SwitchJournalReadStatus.Corrupt)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Corrupt switch journal found: {readResult.ErrorMessage}"), JournalRecoveryStates.NotResolvable);
                }

                if (readResult.Status == SwitchJournalReadStatus.UnsupportedVersion)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Unsupported switch journal schema: {readResult.ErrorMessage}"), JournalRecoveryStates.NotResolvable);
                }

                if (readResult.Status == SwitchJournalReadStatus.IoError)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"I/O error reading switch journal: {readResult.ErrorMessage}"), JournalRecoveryStates.Unknown);
                }

                if (readResult.Status == SwitchJournalReadStatus.Valid)
                {
                    var entry = readResult.Entry!;
                    switch (entry.State)
                    {
                        case SwitchJournalState.RECORDED:
                            try
                            {
                                await _journalStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
                                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Clean, "RECORDED journal cleaned up.", RetainedEntry: null), JournalRecoveryStates.None);
                            }
                            catch (Exception ex)
                            {
                                // Proven clean underlying state. Do NOT quarantine!
                                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Degraded, $"Failed to delete RECORDED journal: {ex.Message}", RetainedEntry: entry), JournalRecoveryStates.ActionRequired);
                            }

                        case SwitchJournalState.CREDENTIAL_APPLYING:
                            _sessionVault.QuarantineUnresolvedMutation();
                            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, "Unresolved CREDENTIAL_APPLYING journal found.", RetainedEntry: entry), JournalRecoveryStates.ActionRequired);

                        case SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT:
                            string? activeAccountId;
                            try
                            {
                                activeAccountId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _sessionVault.QuarantineUnresolvedMutation();
                                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Failed to read metadata for PRECOMMIT journal: {ex.Message}", RetainedEntry: entry), JournalRecoveryStates.Unknown);
                            }

                            if (string.Equals(activeAccountId, entry.TargetAccountId, StringComparison.Ordinal))
                            {
                                // Metadata commit already completed!
                                try
                                {
                                    await _journalStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
                                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Clean, "PRECOMMIT journal cleaned up; target metadata already active.", RetainedEntry: null), JournalRecoveryStates.None);
                                }
                                catch (Exception ex)
                                {
                                    // Proven coherent. Do NOT quarantine!
                                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Degraded, $"Failed to delete PRECOMMIT journal: {ex.Message}", RetainedEntry: entry), JournalRecoveryStates.ActionRequired);
                                }
                            }
                            else
                            {
                                // Active account is source, other, or null. Quarantine!
                                // NEVER call TryFinalizeSwitchAsync during startup!
                                _sessionVault.QuarantineUnresolvedMutation();
                                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"PRECOMMIT journal found but active account '{activeAccountId}' does not match target '{entry.TargetAccountId}'.", RetainedEntry: entry), JournalRecoveryStates.ActionRequired);
                            }

                        case SwitchJournalState.ROLLING_BACK:
                            _sessionVault.QuarantineUnresolvedMutation();
                            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, "Unresolved ROLLING_BACK journal found.", RetainedEntry: entry), JournalRecoveryStates.ActionRequired);

                        case SwitchJournalState.QUARANTINED:
                            _sessionVault.QuarantineUnresolvedMutation();
                            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, "QUARANTINED journal found.", RetainedEntry: entry), JournalRecoveryStates.ActionRequired);

                        default:
                            _sessionVault.QuarantineUnresolvedMutation();
                            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Unrecognized journal state '{entry.State}'.", RetainedEntry: entry), JournalRecoveryStates.Unknown);
                    }
                }

                _sessionVault.QuarantineUnresolvedMutation();
                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Unknown switch journal read status '{readResult.Status}'."), JournalRecoveryStates.Unknown);
            }
            StartupJournalReconciliationResult result;
            Exception? primaryError = null;
            Exception? cleanupError = null;
            try { result = await ReconcileUnderLeaseAsync().ConfigureAwait(false); }
            catch (Exception ex) { primaryError = ex; throw; }
            finally
            {
                try
                {
                    cleanupError = await LeaseCleanup.TryDisposeAsync(switchLease).ConfigureAwait(false);
                    if (cleanupError != null)
                    {
                        _recoveryQuarantine.Mark();
                        _sessionVault.QuarantineUnresolvedMutation();
                    }
                }
                finally { switchLock.Release(); }
                if (cleanupError != null) LeaseCleanup.PreservePrimaryFailure(primaryError, cleanupError);
            }
            if (cleanupError != null)
            {
                result = result with
                {
                    Status = result.Status == StartupJournalReconciliationStatus.Clean
                        ? StartupJournalReconciliationStatus.Quarantined : result.Status,
                    Message = result.Message + " Lease cleanup also failed; manual recovery is required."
                };
                return RecordStartupReconciliationResult(result,
                    _journalRecoveryState == JournalRecoveryStates.None ? JournalRecoveryStates.Unknown : _journalRecoveryState);
            }
            return result;

        }
        finally
        {
            SwitchGate.Release();
        }
    }

    private sealed record CoherenceProofOutcome(
        bool IsProven,
        string? ActiveAccountId,
        string SafeMessage,
        string? ReasonCode = null);

    private async Task<CoherenceProofOutcome> VerifyCoherenceProofAsync(CancellationToken cancellationToken)
    {
        // 1. Persisted active account ID
        string? activeAccountId;
        try
        {
            activeAccountId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new CoherenceProofOutcome(false, null, "Failed to read active account from metadata.", "NO_ACTIVE_ACCOUNT");
        }

        if (string.IsNullOrWhiteSpace(activeAccountId))
        {
            return new CoherenceProofOutcome(false, null, "No active account is set in metadata.", "NO_ACTIVE_ACCOUNT");
        }

        // 2. Corresponding account metadata still exists
        AccountMetadata? activeAccount;
        try
        {
            activeAccount = await _accountStore.GetAccountAsync(activeAccountId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new CoherenceProofOutcome(false, activeAccountId, "The active account was not found in metadata.", "ACTIVE_ACCOUNT_NOT_FOUND");
        }

        if (activeAccount == null)
        {
            return new CoherenceProofOutcome(false, activeAccountId, "The active account was not found in metadata.", "ACTIVE_ACCOUNT_NOT_FOUND");
        }

        // 3. Current enrollment/validation status is still acceptable
        if (activeAccount.ValidationStatus != AccountValidationStatus.Valid || !activeAccount.HasVaultedSession)
        {
            return new CoherenceProofOutcome(false, activeAccountId, "The active account is not validly enrolled or has no vaulted session.", "ACCOUNT_NOT_ENROLLED");
        }

        // 4. Fresh live Antigravity identity still corresponds to the same active account
        AccountIdentityDto? liveIdentity;
        try
        {
            liveIdentity = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new CoherenceProofOutcome(false, activeAccountId, "Live Antigravity identity is unavailable; start Antigravity and ensure an account is active before resolving.", "LIVE_IDENTITY_UNAVAILABLE");
        }

        if (liveIdentity == null || string.IsNullOrWhiteSpace(liveIdentity.Email))
        {
            return new CoherenceProofOutcome(false, activeAccountId, "Live Antigravity identity is unavailable; start Antigravity and ensure an account is active before resolving.", "LIVE_IDENTITY_UNAVAILABLE");
        }

        if (!string.Equals(activeAccount.Email?.Trim(), liveIdentity.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new CoherenceProofOutcome(false, activeAccountId, "Live Antigravity identity does not match the active account metadata.", "LIVE_IDENTITY_MISMATCH");
        }

        // 5. Fresh WinCred & Recovery Vault Payload
        WinCredEntry? winCred = null;
        byte[]? vaultedSession = null;
        try
        {
            try
            {
                winCred = await _winCredReader.ReadCredentialAsync(WinCredTarget, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return new CoherenceProofOutcome(false, activeAccountId, "Windows Credential Manager entry is missing or empty.", "CREDENTIAL_MISSING");
            }

            if (winCred?.Blob == null || winCred.Blob.Length == 0)
            {
                return new CoherenceProofOutcome(false, activeAccountId, "Windows Credential Manager entry is missing or empty.", "CREDENTIAL_MISSING");
            }

            try
            {
                vaultedSession = await _sessionVault.GetSessionForRecoveryAsync(activeAccountId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return new CoherenceProofOutcome(false, activeAccountId, "Vaulted session for the active account is missing or empty.", "VAULT_SESSION_MISSING");
            }

            if (vaultedSession == null || vaultedSession.Length == 0)
            {
                return new CoherenceProofOutcome(false, activeAccountId, "Vaulted session for the active account is missing or empty.", "VAULT_SESSION_MISSING");
            }

            // 6. WinCred payload equals vaulted session using constant-time byte comparison
            bool credsMatch = winCred.Blob.Length == vaultedSession.Length
                           && CryptographicOperations.FixedTimeEquals(winCred.Blob, vaultedSession);

            if (!credsMatch)
            {
                return new CoherenceProofOutcome(false, activeAccountId, "Windows Credential Manager payload does not match the vaulted session.", "CREDENTIAL_MISMATCH");
            }

            return new CoherenceProofOutcome(true, activeAccountId, "Coherence verified.");
        }
        finally
        {
            if (winCred?.Blob != null) CryptographicOperations.ZeroMemory(winCred.Blob);
            if (vaultedSession != null) CryptographicOperations.ZeroMemory(vaultedSession);
        }
    }

    private JournalResolutionResult RecordResolutionResult(JournalResolutionResult result)
    {
        lock (_statusLock)
        {
            if (_journalRecoveryState == JournalRecoveryStates.RestartRequired)
            {
                return result.Status is JournalResolutionStatus.CleanCleanupCompleted or JournalResolutionStatus.NoJournal
                    ? result with { RestartRequired = true } : result;
            }

            switch (result.Status)
            {
                case JournalResolutionStatus.ResolvedRestartRequired:
                    _journalRecoveryState = JournalRecoveryStates.RestartRequired;
                    break;

                case JournalResolutionStatus.CleanCleanupCompleted:
                case JournalResolutionStatus.NoJournal:
                    _journalRecoveryState = JournalRecoveryStates.None;
                    if (IsQuarantinedOrRecoveryUnresolved())
                        result = result with { RestartRequired = true, Message = "Journal cleanup completed, but switching remains quarantined. Restart AG2 Router before switching can resume." };
                    break;

                case JournalResolutionStatus.NotResolvable:
                    _recoveryQuarantine.Mark();
                    _journalRecoveryState = JournalRecoveryStates.NotResolvable;
                    break;

                case JournalResolutionStatus.ProofFailed:
                    _recoveryQuarantine.Mark();
                    _journalRecoveryState = JournalRecoveryStates.ActionRequired;
                    break;

                case JournalResolutionStatus.PersistenceFailure:
                    _recoveryQuarantine.Mark();
                    if (result.ReasonCode == "DELETE_FAILED" || result.ReasonCode == "UNSUPPORTED_PLATFORM")
                    {
                        _journalRecoveryState = JournalRecoveryStates.ActionRequired;
                    }
                    else
                    {
                        _journalRecoveryState = JournalRecoveryStates.Unknown;
                    }
                    break;

                default:
                    _recoveryQuarantine.Mark();
                    _journalRecoveryState = JournalRecoveryStates.Unknown;
                    break;
            }
        }
        return result;
    }

    public async Task<JournalResolutionResult> ResolveQuarantinedJournalAsync(CancellationToken cancellationToken = default)
    {
        await SwitchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string switchResource = _sessionVault.GetVaultPath() + ".switch";
            var switchLock = PathLockRegistry.Get(switchResource);
            if (!await switchLock.WaitAsync(_transactionTimeout, cancellationToken).ConfigureAwait(false))
            {
                return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Lock acquisition timed out during journal resolution.", ReasonCode: "LOCK_TIMEOUT"));
            }

            IAsyncDisposable? switchLease = null;
            async Task<JournalResolutionResult> ResolveUnderLeaseAsync()
            {
                try
                {
                    switchLease = await AcquireRecoveryLeaseAsync(switchResource, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Cross-process lease acquisition failed.", ReasonCode: "LEASE_ACQUISITION_FAILED"));
                }

                SwitchJournalReadResult readResult;
                try
                {
                    readResult = await _journalStore.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "An I/O error occurred while accessing the switch journal.", ReasonCode: "IO_ERROR"));
                }

                if (readResult.Status == SwitchJournalReadStatus.Absent)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.NoJournal, "No switch journal present.", RestartRequired: false));
                }

                if (readResult.Status == SwitchJournalReadStatus.Corrupt)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.NotResolvable, "The switch journal is corrupted and cannot be resolved automatically.", ReasonCode: "CORRUPT_JOURNAL"));
                }

                if (readResult.Status == SwitchJournalReadStatus.UnsupportedVersion)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.NotResolvable, "The switch journal uses an unsupported schema version and cannot be resolved automatically.", ReasonCode: "UNSUPPORTED_VERSION"));
                }

                if (readResult.Status == SwitchJournalReadStatus.IoError)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "An I/O error occurred while accessing the switch journal.", ReasonCode: "IO_ERROR"));
                }

                if (readResult.Status != SwitchJournalReadStatus.Valid || readResult.Entry == null)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "An I/O error occurred while accessing the switch journal.", ReasonCode: "IO_ERROR"));
                }

                var entry = readResult.Entry;

                // F-270-3: Clean-only states alignment with Step 3
                if (entry.State == SwitchJournalState.RECORDED)
                {
                    // No durable credential or metadata mutation began before crash. Clean cleanup.
                    var del = await _journalStore.DeleteIfUnchangedAsync(entry, cancellationToken).ConfigureAwait(false);
                    return RecordResolutionResult(del.Status switch
                    {
                        SwitchJournalDeleteStatus.Deleted => new JournalResolutionResult(JournalResolutionStatus.CleanCleanupCompleted, "Switch journal cleaned up successfully.", RestartRequired: false, ReasonCode: "CLEAN_RECORDED_REMOVED"),
                        SwitchJournalDeleteStatus.Absent => new JournalResolutionResult(JournalResolutionStatus.NoJournal, "No switch journal present.", RestartRequired: false),
                        SwitchJournalDeleteStatus.NotMatched => new JournalResolutionResult(JournalResolutionStatus.ProofFailed, "State changed concurrently during resolution proof.", ReasonCode: "CONCURRENT_MUTATION"),
                        SwitchJournalDeleteStatus.UnsupportedPlatform => new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Conditional switch journal deletion is not supported on this platform.", RestartRequired: false, ReasonCode: "UNSUPPORTED_PLATFORM"),
                        _ => new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Failed to delete the switch journal file.", ReasonCode: "DELETE_FAILED")
                    });
                }

                if (entry.State == SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT)
                {
                    string? currentActiveId = null;
                    try
                    {
                        currentActiveId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Fall through to unresolved proof path
                    }

                    if (string.Equals(currentActiveId, entry.TargetAccountId, StringComparison.Ordinal))
                    {
                        // Metadata commit already completed before crash. Clean cleanup.
                        var del = await _journalStore.DeleteIfUnchangedAsync(entry, cancellationToken).ConfigureAwait(false);
                        return RecordResolutionResult(del.Status switch
                        {
                            SwitchJournalDeleteStatus.Deleted => new JournalResolutionResult(JournalResolutionStatus.CleanCleanupCompleted, "Switch journal cleaned up successfully.", RestartRequired: false, ReasonCode: "CLEAN_COMMITTED_PRECOMMIT_REMOVED"),
                            SwitchJournalDeleteStatus.Absent => new JournalResolutionResult(JournalResolutionStatus.NoJournal, "No switch journal present.", RestartRequired: false),
                            SwitchJournalDeleteStatus.NotMatched => new JournalResolutionResult(JournalResolutionStatus.ProofFailed, "State changed concurrently during resolution proof.", ReasonCode: "CONCURRENT_MUTATION"),
                            SwitchJournalDeleteStatus.UnsupportedPlatform => new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Conditional switch journal deletion is not supported on this platform.", RestartRequired: false, ReasonCode: "UNSUPPORTED_PLATFORM"),
                            _ => new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Failed to delete the switch journal file.", ReasonCode: "DELETE_FAILED")
                        });
                    }
                }

                // Primary Proof-Resolution Cases (CREDENTIAL_APPLYING, unresolved PRECOMMIT, ROLLING_BACK, QUARANTINED):
                // 1. Initial 4-Pillar Proof:
                var initialProof = await VerifyCoherenceProofAsync(cancellationToken).ConfigureAwait(false);
                if (!initialProof.IsProven)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.ProofFailed, initialProof.SafeMessage, ReasonCode: initialProof.ReasonCode));
                }

                // 2. Final 4-Pillar Proof (F-270-1) immediately before delete:
                var finalProof = await VerifyCoherenceProofAsync(cancellationToken).ConfigureAwait(false);
                if (!finalProof.IsProven)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.ProofFailed, finalProof.SafeMessage, ReasonCode: finalProof.ReasonCode));
                }

                if (!string.Equals(finalProof.ActiveAccountId, initialProof.ActiveAccountId, StringComparison.Ordinal))
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.ProofFailed, "State changed concurrently during resolution proof.", ReasonCode: "CONCURRENT_MUTATION"));
                }

                // 3. Conditional Journal Deletion (F-270-2):
                var deleteResult = await _journalStore.DeleteIfUnchangedAsync(entry, cancellationToken).ConfigureAwait(false);
                return RecordResolutionResult(deleteResult.Status switch
                {
                    SwitchJournalDeleteStatus.Deleted => new JournalResolutionResult(
                        JournalResolutionStatus.ResolvedRestartRequired,
                        "Switch journal successfully resolved and removed. Application restart is required before normal routing resumes.",
                        CoherentAccountId: finalProof.ActiveAccountId,
                        RestartRequired: true),
                    SwitchJournalDeleteStatus.NotMatched => new JournalResolutionResult(
                        JournalResolutionStatus.ProofFailed,
                        "State changed concurrently during resolution proof.",
                        ReasonCode: "CONCURRENT_MUTATION"),
                    SwitchJournalDeleteStatus.Absent => new JournalResolutionResult(
                        JournalResolutionStatus.NoJournal,
                        "No switch journal present.",
                        RestartRequired: false),
                    SwitchJournalDeleteStatus.UnsupportedPlatform => new JournalResolutionResult(
                        JournalResolutionStatus.PersistenceFailure,
                        "Conditional switch journal deletion is not supported on this platform.",
                        CoherentAccountId: finalProof.ActiveAccountId,
                        RestartRequired: false,
                        ReasonCode: "UNSUPPORTED_PLATFORM"),
                    _ => new JournalResolutionResult(
                        JournalResolutionStatus.PersistenceFailure,
                        "Failed to delete the switch journal file.",
                        CoherentAccountId: finalProof.ActiveAccountId,
                        RestartRequired: true,
                        ReasonCode: "DELETE_FAILED")
                });
            }
            JournalResolutionResult result;
            Exception? primaryError = null;
            Exception? cleanupError = null;
            try { result = await ResolveUnderLeaseAsync().ConfigureAwait(false); }
            catch (Exception ex) { primaryError = ex; throw; }
            finally
            {
                bool reusable = false;
                try
                {
                    if (BeforeLeaseReleaseAsync is { } beforeRelease)
                    {
                        try { await beforeRelease().ConfigureAwait(false); } catch { }
                    }
                    string? completedTransactionId;
                    lock (_statusLock) completedTransactionId = _activeTransactionId == null ? _lastResult?.TransactionId : null;
                    reusable = completedTransactionId != null &&
                        await ProveTransactionReusableAsync(completedTransactionId).ConfigureAwait(false);
                }
                catch (Exception ex) { cleanupError = ex; }
                try
                {
                    cleanupError = await LeaseCleanup.TryDisposeAsync(switchLease, cleanupError).ConfigureAwait(false);
                    lock (_statusLock)
                    {
                        if (cleanupError == null && reusable && _activeTransactionId == null && !IsQuarantinedOrRecoveryUnresolved() &&
                            _currentState is NativeSwitchStates.Complete or NativeSwitchStates.RolledBack or NativeSwitchStates.Failed)
                            _currentState = NativeSwitchStates.Idle;
                    }
                    if (cleanupError != null)
                    {
                        _recoveryQuarantine.Mark();
                        _sessionVault.QuarantineUnresolvedMutation();
                    }
                }
                finally { switchLock.Release(); }
                if (cleanupError != null) LeaseCleanup.PreservePrimaryFailure(primaryError, cleanupError);
            }
            if (cleanupError != null)
            {
                bool failed = result.Status is JournalResolutionStatus.ProofFailed or
                    JournalResolutionStatus.NotResolvable or JournalResolutionStatus.PersistenceFailure;
                return RecordResolutionResult(result with
                {
                    Status = failed ? result.Status : JournalResolutionStatus.PersistenceFailure,
                    ReasonCode = failed ? result.ReasonCode : "LEASE_CLEANUP_FAILED",
                    RestartRequired = true,
                    Message = result.Message + " Lease cleanup also failed; manual recovery is required."
                });
            }
            return result;

        }
        finally
        {
            SwitchGate.Release();
        }
    }


    public async Task<NativeSwitchResult> SwitchAsync(
        string targetAccountId,
        CancellationToken cancellationToken = default)
        => await ObserveSwitchAsync(targetAccountId, null, null, null, null, cancellationToken).ConfigureAwait(false);

    public Task<NativeSwitchResult> SwitchAutomaticallyAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<bool> planIsCurrent,
        CancellationToken cancellationToken = default)
        => ObserveSwitchAsync(targetAccountId, expectedActiveAccountId, planIsCurrent, null, null, cancellationToken);

    public Task<NativeSwitchResult> SwitchAutomaticallyAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<bool> planIsCurrent,
        string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
        CancellationToken cancellationToken = default)
        => ObserveSwitchAsync(targetAccountId, expectedActiveAccountId, planIsCurrent, requiredWorkloadModelKey, minimumCandidateQuotaPercent, cancellationToken);

    public Task<NativeSwitchResult> SwitchAutomaticallyAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<bool> planIsCurrent,
        string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
        Func<CancellationToken, Task<IDisposable>> acquireInterruptionAdmissionAsync,
        CancellationToken cancellationToken = default)
        => ObserveSwitchAsync(targetAccountId, expectedActiveAccountId, planIsCurrent,
            requiredWorkloadModelKey, minimumCandidateQuotaPercent, cancellationToken,
            acquireInterruptionAdmissionAsync ?? throw new ArgumentNullException(nameof(acquireInterruptionAdmissionAsync)));

    public bool CanAdmitSwitch(out string? blockingReason)
    {
        lock (_statusLock)
        {
            if (_isShuttingDown)
            {
                blockingReason = "Switch blocked: coordinator is shutting down.";
                return false;
            }

            if (!string.Equals(_journalRecoveryState, JournalRecoveryStates.None, StringComparison.Ordinal))
            {
                blockingReason = _journalRecoveryState switch
                {
                    JournalRecoveryStates.ActionRequired => "Switch blocked: journal recovery action is required.",
                    JournalRecoveryStates.NotResolvable => "Switch blocked: switch journal is not resolvable.",
                    JournalRecoveryStates.RestartRequired => "Switch blocked: application restart is required after journal resolution.",
                    _ => "Switch blocked: journal recovery state is unknown."
                };
                return false;
            }

            if (_activeTransactionId != null || !string.Equals(_currentState, NativeSwitchStates.Idle, StringComparison.Ordinal))
            {
                blockingReason = "Switch blocked: another switch transaction is currently in progress.";
                return false;
            }
        }

        if (_recoveryQuarantine.IsMarked || _sessionVault.IsQuarantined ||
            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath() + ".switch").IsMarked ||
            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath()).IsMarked)
        {
            blockingReason = "Switch blocked: account lifecycle is quarantined.";
            return false;
        }

        if (SwitchGate.CurrentCount == 0)
        {
            blockingReason = "Switch blocked: another switch transaction is currently in progress.";
            return false;
        }

        blockingReason = null;
        return true;
    }

    private bool IsRecoveryAdmissionBlocked() => !CanAdmitSwitch(out _);

    private bool IsQuarantinedOrRecoveryUnresolved()
    {
        string recoveryState;
        lock (_statusLock) recoveryState = _journalRecoveryState;
        return !string.Equals(recoveryState, JournalRecoveryStates.None, StringComparison.Ordinal) ||
            _recoveryQuarantine.IsMarked ||
            _sessionVault.IsQuarantined ||
            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath() + ".switch").IsMarked ||
            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath()).IsMarked;
    }

    private async Task<NativeSwitchResult> ObserveSwitchAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<bool>? planIsCurrent,
        string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IDisposable>>? acquireInterruptionAdmissionAsync = null)
    {
        if (!CanAdmitSwitch(out var blockingReason))
        {
            if (_isShuttingDown)
            {
                string now = DateTimeOffset.UtcNow.ToString("O");
                return new NativeSwitchResult(null, false, SwitchResultCodes.Cancelled,
                    NativeSwitchStates.Failed, targetAccountId?.Trim() ?? string.Empty, null, null, null,
                    blockingReason ?? "Switch coordinator is shutting down.", [], now, now);
            }
            if (IsQuarantinedOrRecoveryUnresolved())
            {
                return RecoveryUncertain(targetAccountId, blockingReason);
            }
            if (!string.Equals(_currentState, NativeSwitchStates.Idle, StringComparison.Ordinal) ||
                _activeTransactionId != null ||
                SwitchGate.CurrentCount == 0)
            {
                string now = DateTimeOffset.UtcNow.ToString("O");
                return new NativeSwitchResult(null, false, SwitchResultCodes.SwitchInProgress,
                    GetStatus().CurrentState, targetAccountId?.Trim() ?? string.Empty, null, null, null,
                    blockingReason ?? "Another account switch is already in progress.", [], now, now);
            }
            return RecoveryUncertain(targetAccountId, blockingReason);
        }

        // The inner task owns every switch gate/lease until it actually finishes.
        // Timeout detaches only the caller, never the mutation or its serialization.
        Task<NativeSwitchResult> operation = SwitchCoreAsync(
            targetAccountId, expectedActiveAccountId, planIsCurrent, requiredWorkloadModelKey, minimumCandidateQuotaPercent, cancellationToken,
            acquireInterruptionAdmissionAsync);
        try
        {
            return await operation.WaitAsync(_transactionTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (operation.IsCompleted)
                return await operation.ConfigureAwait(false);
            _recoveryQuarantine.Mark();
            var uncertain = RecoveryUncertain(targetAccountId);
            lock (_statusLock)
            {
                _currentState = NativeSwitchStates.Failed;
                _lastResult = uncertain;
                if (_journalRecoveryState != JournalRecoveryStates.RestartRequired)
                {
                    _journalRecoveryState = JournalRecoveryStates.Unknown;
                }
            }
            _ = operation.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return uncertain;
        }
    }

    private NativeSwitchResult RecoveryUncertain(string? targetAccountId, string? message = null)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        string? activeTransactionId;
        lock (_statusLock) activeTransactionId = _activeTransactionId;
        return new NativeSwitchResult(activeTransactionId, false,
            SwitchResultCodes.SwitchFailedRollbackFailed, NativeSwitchStates.Failed,
            targetAccountId?.Trim() ?? string.Empty, null, null, null,
            message ?? "Switch outcome is unresolved; manual recovery is required before another switch.",
            [], now, now, ManualRecoveryRequired: true);
    }

    private async Task<NativeSwitchResult> SwitchCoreAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<bool>? planIsCurrent,
        string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IDisposable>>? acquireInterruptionAdmissionAsync = null)
    {
        if (!CanAdmitSwitch(out var blockingReason))
        {
            if (_isShuttingDown)
            {
                string blockedNow = DateTimeOffset.UtcNow.ToString("O");
                return new NativeSwitchResult(null, false, SwitchResultCodes.Cancelled,
                    NativeSwitchStates.Failed, targetAccountId?.Trim() ?? string.Empty, null, null, null,
                    blockingReason ?? "Switch coordinator is shutting down.", [], blockedNow, blockedNow);
            }
            if (IsQuarantinedOrRecoveryUnresolved())
            {
                return RecoveryUncertain(targetAccountId, blockingReason);
            }
            if (!string.Equals(_currentState, NativeSwitchStates.Idle, StringComparison.Ordinal) ||
                _activeTransactionId != null ||
                SwitchGate.CurrentCount == 0)
            {
                string blockedNow = DateTimeOffset.UtcNow.ToString("O");
                return new NativeSwitchResult(null, false, SwitchResultCodes.SwitchInProgress,
                    GetStatus().CurrentState, targetAccountId?.Trim() ?? string.Empty, null, null, null,
                    blockingReason ?? "Another account switch is already in progress.", [], blockedNow, blockedNow);
            }
            return RecoveryUncertain(targetAccountId, blockingReason);
        }
        string requestedId = targetAccountId?.Trim() ?? string.Empty;
        string now = DateTimeOffset.UtcNow.ToString("O");
        bool entered;
        try
        {
            entered = await SwitchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new NativeSwitchResult(null, false, SwitchResultCodes.Cancelled,
                NativeSwitchStates.Failed, requestedId, null, null, null,
                "Switch request was cancelled before mutation.", [], now,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        if (!entered)
        {
            return new NativeSwitchResult(null, false, SwitchResultCodes.SwitchInProgress,
                GetStatus().CurrentState, requestedId, null, null, null,
                "Another account switch is already in progress.", [], now, DateTimeOffset.UtcNow.ToString("O"));
        }

        try
        {
            if (_isShuttingDown)
            {
                return new NativeSwitchResult(null, false, SwitchResultCodes.Cancelled,
                    NativeSwitchStates.Failed, requestedId, null, null, null,
                    "Switch coordinator is shutting down.", [], now, DateTimeOffset.UtcNow.ToString("O"));
            }

            string transactionId = Guid.NewGuid().ToString("D");
            var stages = new List<string>();
            AccountMetadata? target = null;
            AccountMetadata? sourceAccount = null;
            string? previousAccountId = null;
            string? previousEmail = null;
            WinCredEntry? originalCredential = null;
            WinCredEntry? targetCredential = null;
            byte[]? targetSession = null;
            AG2ProcessSnapshot? processSnapshot = null;
            AG2ProcessGeneration? replacementGeneration = null;
            bool processTransitionStarted = false;
            bool credentialWriteAttempted = false;
            CrossProcessFileLease? switchLease = null;

            string switchResource = _sessionVault.GetVaultPath() + ".switch";
            var switchLock = PathLockRegistry.Get(switchResource);
            if (!await switchLock.WaitAsync(_transactionTimeout, cancellationToken).ConfigureAwait(false))
            {
                _recoveryQuarantine.Mark();
                var uncertain = RecoveryUncertain(requestedId);
                lock (_statusLock)
                {
                    _currentState = NativeSwitchStates.Failed;
                    _lastResult = uncertain;
                    if (_journalRecoveryState != JournalRecoveryStates.RestartRequired)
                    {
                        _journalRecoveryState = JournalRecoveryStates.Unknown;
                    }
                }
                return uncertain;
            }

            SetActive(transactionId, NativeSwitchStates.Preflight);
            try
            {
            if (IsQuarantinedOrRecoveryUnresolved())
                throw new SwitchRejectedException(SwitchResultCodes.SwitchFailedRollbackFailed,
                    "Account lifecycle is unresolved; manual recovery is required.");
            switchLease = await CrossProcessFileLease
                .AcquireAsync(switchResource, cancellationToken)
                .ConfigureAwait(false);
            if (IsQuarantinedOrRecoveryUnresolved())
                throw new SwitchRejectedException(SwitchResultCodes.SwitchFailedRollbackFailed,
                    "Account lifecycle is unresolved; manual recovery is required.");

            if (planIsCurrent != null && !planIsCurrent())
                throw new SwitchRejectedException(SwitchResultCodes.Cancelled, "Automatic switch plan became stale.");

            if (string.IsNullOrWhiteSpace(requestedId))
            {
                throw new SwitchRejectedException(SwitchResultCodes.TargetNotFound, "Target account was not specified.");
            }

            target = await _accountStore.GetAccountAsync(requestedId, cancellationToken).ConfigureAwait(false)
                ?? throw new SwitchRejectedException(SwitchResultCodes.TargetNotFound, "Target account was not found.");
            if (!await _sessionVault.HasSessionAsync(target.Id, cancellationToken).ConfigureAwait(false))
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TargetNotVaulted,
                    "Target account does not have a valid vaulted session.");
            }

            previousAccountId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
            if (planIsCurrent != null &&
                (!planIsCurrent() || !string.Equals(previousAccountId, expectedActiveAccountId, StringComparison.Ordinal)))
                throw new SwitchRejectedException(SwitchResultCodes.Cancelled, "Automatic switch plan became stale.");
            sourceAccount = previousAccountId == null ? null :
                await _accountStore.GetAccountAsync(previousAccountId, cancellationToken).ConfigureAwait(false);
            var currentIdentity = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            previousEmail = currentIdentity?.Email;
            if (!SourceIdentityMatches(sourceAccount, currentIdentity))
                throw new SwitchRejectedException(SwitchResultCodes.TelemetryUnavailable,
                    "Live identity does not match the active account metadata.");
            if (previousAccountId == target.Id ||
                string.Equals(previousEmail, target.Email, StringComparison.OrdinalIgnoreCase))
            {
                throw new SwitchRejectedException(SwitchResultCodes.AlreadyActive, "Target account is already active.");
            }

            var status = await _adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.Connected || status.Status is "DEGRADED" or "OFFLINE" or "ERROR" || currentIdentity == null)
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TelemetryUnavailable,
                    "Antigravity telemetry is unavailable or degraded.");
            }
            var activity = await _adapter.GetActivityStateAsync(cancellationToken).ConfigureAwait(false);
            if (activity.RunningTrajectories > 0 ||
                string.Equals(activity.State, "BUSY", StringComparison.OrdinalIgnoreCase))
            {
                throw new SwitchRejectedException(SwitchResultCodes.Ag2Busy, "Antigravity must be IDLE before switching.");
            }
            if (!string.Equals(activity.State, "IDLE", StringComparison.OrdinalIgnoreCase))
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TelemetryUnavailable,
                    "Antigravity activity telemetry is unknown or degraded.");
            }
            stages.Add("PREFLIGHT_VERIFIED");

            SetState(NativeSwitchStates.Snapshotting);
            try
            {
                processSnapshot = await _processLifecycle.CaptureVerifiedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.UnsafeProcess,
                    $"Antigravity process provenance is unsafe: {AG2Security.SanitizeError(ex)}");
            }

            originalCredential = await CaptureCurrentCredentialAsync(cancellationToken)
                .ConfigureAwait(false);
            stages.Add("ROLLBACK_SNAPSHOT_CAPTURED");

            // Re-check both process generation and idle telemetry immediately before mutation.
            try
            {
                await _processLifecycle.RevalidateAsync(processSnapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (AG2ProcessLifecycleException ex)
            {
                throw new SwitchRejectedException(SwitchResultCodes.UnsafeProcess,
                    $"Antigravity process generation became unsafe: {AG2Security.SanitizeError(ex)}");
            }
            var finalActivity = await _adapter.GetActivityStateAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _processLifecycle.RevalidateAsync(processSnapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (AG2ProcessLifecycleException ex)
            {
                throw new SwitchRejectedException(SwitchResultCodes.UnsafeProcess,
                    $"Antigravity process generation became unsafe: {AG2Security.SanitizeError(ex)}");
            }
            if (finalActivity.RunningTrajectories > 0 ||
                string.Equals(finalActivity.State, "BUSY", StringComparison.OrdinalIgnoreCase))
            {
                throw new SwitchRejectedException(SwitchResultCodes.Ag2Busy, "Antigravity became busy before mutation.");
            }
            if (!string.Equals(finalActivity.State, "IDLE", StringComparison.OrdinalIgnoreCase))
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TelemetryUnavailable,
                    "Antigravity activity telemetry became unknown before mutation.");
            }

            var finalIdentity = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            if (!SourceIdentityMatches(sourceAccount, finalIdentity))
                throw new SwitchRejectedException(SwitchResultCodes.TelemetryUnavailable,
                    "Live identity changed before credential mutation.");

            if (planIsCurrent != null &&
                (!planIsCurrent() || !string.Equals(
                    await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false),
                    expectedActiveAccountId, StringComparison.Ordinal)))
                throw new SwitchRejectedException(SwitchResultCodes.Cancelled, "Automatic switch plan became stale before mutation.");

            targetSession = await _sessionVault.GetSessionAsync(target.Id, cancellationToken).ConfigureAwait(false);
            if (targetSession == null || targetSession.Length == 0 || targetSession.Length > MaxCredentialBlobBytes)
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TargetNotVaulted,
                    "Target vaulted session is missing, empty, or outside credential bounds.");
            }
            // Vault loading is awaitable. Its result does not prove the live source
            // identity still matches the metadata used for this transaction.
            var identityAtStop = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            var activeAtStop = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
            var identityAfterActiveRead = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            if (!SourceIdentityMatches(sourceAccount, identityAtStop) ||
                !SourceIdentityMatches(sourceAccount, identityAfterActiveRead) ||
                !string.Equals(activeAtStop, previousAccountId, StringComparison.Ordinal))
                throw new SwitchRejectedException(SwitchResultCodes.TelemetryUnavailable,
                    "Live or active identity changed before process stop.");
            // Quiesce the verified source before changing its credential. This prevents the
            // old generation from racing the transaction by persisting a newer session.
            _shutdownCts.Token.ThrowIfCancellationRequested();
            SetState(NativeSwitchStates.StoppingProcess);

            // Decouple from caller cancellation token at mutation boundary.
            var mutationTimeout = _processTimeout + _verificationTimeout + TimeSpan.FromSeconds(10);
            using var mutationCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            mutationCts.CancelAfter(mutationTimeout);
            var mutationToken = mutationCts.Token;

            // Phase Transition a: RECORDED (strictly before StopVerifiedAsync)
            await WriteJournalStateAsync(
                transactionId,
                SwitchJournalState.RECORDED,
                sourceAccount!.Id,
                target.Id,
                quarantineReasonCode: null,
                cancellationToken: mutationToken).ConfigureAwait(false);

            // R02: Interruption admission lifecycle
            // When executing an automatic switch, acquireInterruptionAdmissionAsync is provided.
            // Admission is acquired inside verifyBeforeKillAsync, held across the process Kill() invocation,
            // and explicitly released in onStopIssued before waiting for the process to exit.
            // The finally block ensures that if any check throws, the admission lease is deterministically disposed.
            IDisposable? interruptionAdmission = null;
            try
            {
                await _processLifecycle.StopVerifiedAsync(processSnapshot, _processTimeout, mutationToken,
                        async token =>
                        {
                            if (_recoveryQuarantine.IsMarked || _sessionVault.IsQuarantined)
                                throw new SwitchRejectedException(SwitchResultCodes.SwitchFailedRollbackFailed,
                                    "Account lifecycle is unresolved at the process-stop boundary.");
                            string? authoritativeId = await _accountStore.GetActiveAccountIdAsync(token)
                                .ConfigureAwait(false);
                            var live = await _adapter.GetCurrentAccountAsync(token).ConfigureAwait(false);
                            if (!string.Equals(authoritativeId, previousAccountId, StringComparison.Ordinal) ||
                                !SourceIdentityMatches(sourceAccount, live))
                                throw new SwitchRejectedException(SwitchResultCodes.TelemetryUnavailable,
                                    "Live or active identity changed at the process-stop boundary.");
                            var stopActivity = await _adapter.GetActivityStateAsync(token).ConfigureAwait(false);
                            if (stopActivity.RunningTrajectories > 0 ||
                                string.Equals(stopActivity.State, "BUSY", StringComparison.OrdinalIgnoreCase))
                                throw new SwitchRejectedException(SwitchResultCodes.Ag2Busy,
                                    "Antigravity became busy at the process-stop boundary.");
                            if (!string.Equals(stopActivity.State, "IDLE", StringComparison.OrdinalIgnoreCase))
                                throw new SwitchRejectedException(SwitchResultCodes.TelemetryUnavailable,
                                    "Antigravity activity was not proven idle at the process-stop boundary.");
                            // Caller cancellation still applies while waiting for final admission:
                            // no source mutation has occurred yet. Do not hold router state ownership.
                            if (acquireInterruptionAdmissionAsync != null)
                            {
                                using var admissionCts = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                                interruptionAdmission = await acquireInterruptionAdmissionAsync(admissionCts.Token)
                                    .ConfigureAwait(false)
                                    ?? throw new InvalidOperationException("Automatic interruption admission did not provide ownership.");
                                admissionCts.Token.ThrowIfCancellationRequested();
                            }
                            if (planIsCurrent != null && !planIsCurrent())
                                throw new SwitchRejectedException(SwitchResultCodes.Cancelled,
                                    "Automatic quota evidence changed or expired at the process-stop boundary.");
                            if (_recoveryQuarantine.IsMarked || _sessionVault.IsQuarantined)
                                throw new SwitchRejectedException(SwitchResultCodes.SwitchFailedRollbackFailed,
                                    "Account lifecycle became unresolved at the process-stop boundary.");
                        },
                        onStopAttempted: () => processTransitionStarted = true,
                        onStopIssued: () =>
                        {
                            // Release only AFTER the kill request, never at the pre-kill notification.
                            Interlocked.Exchange(ref interruptionAdmission, null)?.Dispose();
                        })
                    .ConfigureAwait(false);
            }
            finally { Interlocked.Exchange(ref interruptionAdmission, null)?.Dispose(); }
            stages.Add("SOURCE_PROCESS_STOPPED");

            // The source may have refreshed WinCred after the provisional preflight read.
            // Once it is proven stopped, refresh the rollback snapshot before any overwrite.
            var quiescedCredential = await CaptureCurrentCredentialAsync(mutationToken)
                .ConfigureAwait(false);
            ZeroCredential(originalCredential);
            originalCredential = quiescedCredential;
            stages.Add("ROLLBACK_SNAPSHOT_REFRESHED_AFTER_QUIESCE");

            targetCredential = new WinCredEntry(
                WinCredTarget,
                originalCredential.Type,
                VaultConstants.DefaultAg2WinCredUserName,
                originalCredential.Persistence,
                targetSession);
            ValidateCredential(targetCredential, requireConfiguredTarget: true);

            // Phase Transition b: CREDENTIAL_APPLYING (strictly before WriteCredentialAsync)
            await WriteJournalStateAsync(
                transactionId,
                SwitchJournalState.CREDENTIAL_APPLYING,
                sourceAccount!.Id,
                target.Id,
                quarantineReasonCode: null,
                cancellationToken: mutationToken).ConfigureAwait(false);

            SetState(NativeSwitchStates.ApplyingCredential);
            credentialWriteAttempted = true;
            bool written = await _winCredWriter.WriteCredentialAsync(targetCredential, mutationToken)
                .ConfigureAwait(false);
            if (!written) throw new InvalidOperationException("Credential writer reported failure.");
            await RequireCredentialEqualsAsync(targetCredential, mutationToken).ConfigureAwait(false);
            stages.Add("CREDENTIAL_APPLIED_AND_VERIFIED");

            SetState(NativeSwitchStates.Restarting);
            replacementGeneration = await _processLifecycle.LaunchAsync(processSnapshot, mutationToken)
                .ConfigureAwait(false);
            replacementGeneration = await _processLifecycle.WaitForHealthyReplacementAsync(
                processSnapshot,
                replacementGeneration,
                _verificationTimeout,
                mutationToken).ConfigureAwait(false);
            stages.Add("REPLACEMENT_PROCESS_VERIFIED");

            SetState(NativeSwitchStates.Verifying);
            await VerifyIdentityAsync(target.Email, replacementGeneration, mutationToken).ConfigureAwait(false);
            stages.Add("TARGET_IDENTITY_VERIFIED");

            List<AccountModelQuotaObservation>? verifiedTargetObservations = null;
            if (!string.IsNullOrWhiteSpace(requiredWorkloadModelKey))
            {
                AccountModelQuotaObservation? cachedEvidence = null;
                try
                {
                    if (_quotaObservationStore != null)
                        cachedEvidence = await _quotaObservationStore.GetObservationAsync(target.Id,
                            requiredWorkloadModelKey, mutationToken).ConfigureAwait(false);
                    verifiedTargetObservations = await VerifyTargetQuotaAsync(
                        target, replacementGeneration, requiredWorkloadModelKey,
                        minimumCandidateQuotaPercent, stages, mutationToken).ConfigureAwait(false);
                }
                catch (SwitchVerificationException)
                {
                    if (_quotaObservationStore != null && cachedEvidence != null)
                    {
                        try
                        {
                            // Do not invalidate a newer independent observation. Persist
                            // rejection before rollback; cooldown expiry must not revive it.
                            await _quotaObservationStore.InvalidateIfUnchangedAsync(cachedEvidence,
                                _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                            stages.Add("TARGET_QUOTA_EVIDENCE_INVALIDATED");
                        }
                        catch
                        {
                            _recoveryQuarantine.Mark();
                            stages.Add("TARGET_QUOTA_INVALIDATION_FAILED");
                        }
                    }
                    throw;
                }
            }

            SetState(NativeSwitchStates.Finalizing);
            if (!await _processLifecycle.IsGenerationCurrentAsync(replacementGeneration, mutationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Process generation changed after target verification and before metadata finalization.");
            }

            // Phase Transition c: TARGET_IDENTITY_VERIFIED_PRECOMMIT (strictly before TryFinalizeSwitchAsync)
            await WriteJournalStateAsync(
                transactionId,
                SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT,
                sourceAccount!.Id,
                target.Id,
                quarantineReasonCode: null,
                cancellationToken: mutationToken).ConfigureAwait(false);

            var updated = await _accountStore.TryFinalizeSwitchAsync(previousAccountId, target.Id, new UpdateAccountInput(
                ValidationStatus: AccountValidationStatus.Valid,
                HasVaultedSession: true,
                LastActiveAt: DateTimeOffset.UtcNow.ToString("O")), mutationToken).ConfigureAwait(false);
            if (updated == null)
                throw new InvalidOperationException("Account metadata changed during atomic switch finalization.");
            stages.Add("METADATA_COMMITTED");

            if (_quotaObservationStore != null && verifiedTargetObservations != null && verifiedTargetObservations.Count > 0)
            {
                try
                {
                    await _quotaObservationStore.RecordCompleteSnapshotAsync(
                        target.Id, verifiedTargetObservations, verifiedTargetObservations[0].ObservedAtUtc,
                        "LiveTargetVerification", mutationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort derived telemetry persistence; transaction is already committed.
                }
            }

            // Phase Transition d: Success Deletion (only after TryFinalizeSwitchAsync succeeds)
            try
            {
                await _journalStore.DeleteAsync(mutationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Deletion failure: do not fail switch, do not revert metadata.
                // TARGET_IDENTITY_VERIFIED_PRECOMMIT remains on disk for Step 3 startup reconciliation.
            }

            return Terminal(transactionId, true, SwitchResultCodes.Success, NativeSwitchStates.Complete,
                target.Id, target.Email, previousAccountId, previousEmail,
                "Target account was activated and verified.", stages, now);
        }
        catch (SwitchRejectedException ex) when (!processTransitionStarted)
        {
            try { await _journalStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            return Terminal(transactionId, false, ex.Code, NativeSwitchStates.Failed,
                requestedId, target?.Email, previousAccountId, previousEmail,
                AG2Security.RedactSensitiveText(ex.Message), stages, now);
        }
        catch (OperationCanceledException) when (!processTransitionStarted)
        {
            try { await _journalStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            return Terminal(transactionId, false, SwitchResultCodes.Cancelled, NativeSwitchStates.Failed,
                requestedId, target?.Email, previousAccountId, previousEmail,
                "Switch request was cancelled before credential mutation.", stages, now);
        }
        catch (Exception switchError)
        {
            if (!processTransitionStarted || originalCredential == null || processSnapshot == null)
            {
                try { await _journalStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                return Terminal(transactionId, false,
                    switchError is OperationCanceledException ? SwitchResultCodes.Cancelled : SwitchResultCodes.TelemetryUnavailable,
                    NativeSwitchStates.Failed, requestedId, target?.Email, previousAccountId, previousEmail,
                    SafeFailure(switchError), stages, now);
            }

            SetState(NativeSwitchStates.RollingBack);
            stages.Add("ROLLBACK_STARTED");

            // Phase Transition e: ROLLING_BACK (strictly before QuiesceForRollbackAsync or restoring credentials)
            try
            {
                await WriteJournalStateAsync(
                    transactionId,
                    SwitchJournalState.ROLLING_BACK,
                    sourceAccount?.Id ?? previousAccountId ?? string.Empty,
                    target?.Id ?? requestedId,
                    quarantineReasonCode: null,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackJournalError)
            {
                // CRITICAL SAFETY RULE: If writing ROLLING_BACK fails/throws:
                // Do NOT call QuiesceForRollbackAsync, do NOT restore credentials under unrecorded rollback!
                // Leave prior conservative journal state (CREDENTIAL_APPLYING or RECORDED) on disk.
                _recoveryQuarantine.Mark();
                stages.Add("ROLLBACK_FAILED");
                return Terminal(transactionId, false, SwitchResultCodes.SwitchFailedRollbackFailed,
                    NativeSwitchStates.Failed, requestedId, target?.Email, previousAccountId, previousEmail,
                    $"Switch and rollback failed; manual recovery is required. {SafeFailure(rollbackJournalError)}",
                    stages, now);
            }

            try
            {
                using var rollbackCts = new CancellationTokenSource(_rollbackTimeout);
                var rollbackToken = rollbackCts.Token;
                await _processLifecycle.QuiesceForRollbackAsync(
                    processSnapshot,
                    replacementGeneration,
                    _processTimeout,
                    rollbackToken).ConfigureAwait(false);
                stages.Add("SWITCH_PROCESS_QUIESCED");

                if (credentialWriteAttempted)
                {
                    if (targetCredential == null)
                        throw new InvalidOperationException("Applied credential snapshot was unavailable for rollback.");
                    await RestoreCredentialConditionallyAsync(originalCredential, targetCredential, rollbackToken)
                        .ConfigureAwait(false);
                    stages.Add("ORIGINAL_CREDENTIAL_RESTORED");
                }
                else
                {
                    stages.Add("CREDENTIAL_UNCHANGED");
                }

                var restoredGeneration = await _processLifecycle.RestoreAsync(
                    processSnapshot,
                    _verificationTimeout,
                    rollbackToken).ConfigureAwait(false);
                stages.Add("SOURCE_PROCESS_RESTORED");

                if (string.IsNullOrWhiteSpace(previousEmail))
                {
                    throw new InvalidOperationException("Original identity was unavailable for rollback verification.");
                }
                await VerifyIdentityAsync(previousEmail, restoredGeneration, rollbackToken)
                    .ConfigureAwait(false);
                stages.Add("SOURCE_IDENTITY_VERIFIED");

                // Phase Transition f: Rollback Success Deletion
                try
                {
                    // If durable evidence rejection failed, retain the recovery
                    // journal so restart cannot clear an in-memory-only quarantine
                    // and route using the same disproven healthy observation.
                    if (!_recoveryQuarantine.IsMarked)
                        await _journalStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // If deletion fails/throws: log warning; rollback succeeded.
                }

                string failureDetail = SafeFailure(switchError);
                string message = string.IsNullOrWhiteSpace(failureDetail)
                    ? "Switch failed; the original credential and verified source identity were restored."
                    : $"Switch failed; the original credential and verified source identity were restored. {failureDetail}";

                return Terminal(transactionId, false, SwitchResultCodes.SwitchFailedRolledBack,
                    NativeSwitchStates.RolledBack, requestedId, target?.Email, previousAccountId, previousEmail,
                    message, stages, now);
            }
            catch (Exception rollbackError)
            {
                // Phase Transition g: Rollback Failure / Uncertain Quarantine
                stages.Add("ROLLBACK_FAILED");
                _recoveryQuarantine.Mark();

                try
                {
                    await WriteJournalStateAsync(
                        transactionId,
                        SwitchJournalState.QUARANTINED,
                        sourceAccount?.Id ?? previousAccountId ?? string.Empty,
                        target?.Id ?? requestedId,
                        quarantineReasonCode: "ROLLBACK_FAILED",
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // If writing QUARANTINED throws: mark in-process quarantine anyway (already marked),
                    // return uncertain/manual-recovery failure.
                }

                return Terminal(transactionId, false, SwitchResultCodes.SwitchFailedRollbackFailed,
                    NativeSwitchStates.Failed, requestedId, target?.Email, previousAccountId, previousEmail,
                    $"Switch and rollback failed; manual recovery is required. {SafeFailure(rollbackError)}",
                    stages, now);
            }
        }
        finally
        {
            ZeroCredential(originalCredential);
            if (targetCredential != null && !ReferenceEquals(targetCredential.Blob, targetSession))
            {
                ZeroCredential(targetCredential);
            }
            if (targetSession != null) CryptographicOperations.ZeroMemory(targetSession);
            Exception? cleanupError = null;
            bool journalProvenAbsent = false;
            try
            {
                if (BeforeLeaseReleaseAsync is { } beforeRelease)
                    await beforeRelease().ConfigureAwait(false);
                // Retain switch ownership while proving that terminal cleanup left
                // no recovery artifact. A terminal result alone is not that proof.
                journalProvenAbsent = await ProveTransactionReusableAsync(transactionId).ConfigureAwait(false);
                if (switchLease != null) await switchLease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupError = ex;
                // Even an injected/diagnostic release failure must still attempt
                // to release the actual lease before lifting in-process ownership.
                if (switchLease != null)
                {
                    try { await switchLease.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception releaseError)
                    {
                        cleanupError = new AggregateException(ex, releaseError);
                    }
                }
            }
            finally
            {
                switchLock.Release();
                lock (_statusLock)
                {
                    _activeTransactionId = null;
                    if (cleanupError == null && journalProvenAbsent &&
                        _lastResult?.TransactionId == transactionId && !_lastResult.ManualRecoveryRequired &&
                        _journalRecoveryState == JournalRecoveryStates.None &&
                        !IsQuarantinedOrRecoveryUnresolved() &&
                        _currentState is NativeSwitchStates.Complete or NativeSwitchStates.RolledBack or NativeSwitchStates.Failed)
                        _currentState = NativeSwitchStates.Idle;
                }
            }
            if (cleanupError != null)
            {
                lock (_statusLock)
                {
                    if (_lastResult?.TransactionId == transactionId && _lastResult.ManualRecoveryRequired)
                    {
                        _lastResult = _lastResult with
                        {
                            Message = _lastResult.Message + " Cleanup also failed; manual recovery remains required."
                        };
                    }
                    else
                    {
                        if (_lastResult?.TransactionId == transactionId)
                        {
                            _lastResult = _lastResult with
                            {
                                Success = false,
                                Code = SwitchResultCodes.SwitchFailedRollbackFailed,
                                State = NativeSwitchStates.Failed,
                                ManualRecoveryRequired = true,
                                Message = "Switch cleanup failed; manual recovery is required."
                            };
                        }
                        _currentState = NativeSwitchStates.Failed;
                        throw new InvalidOperationException(
                            "Switch cleanup failed; outcome requires manual recovery.", cleanupError);
                    }
                }
            }
        }
        }
        finally
        {
            SwitchGate.Release();
        }
    }

    private async Task<bool> ProveTransactionReusableAsync(string transactionId)
    {
        lock (_statusLock)
        {
            if (_lastResult?.TransactionId != transactionId || _lastResult.ManualRecoveryRequired ||
                IsQuarantinedOrRecoveryUnresolved()) return false;
        }
        SwitchJournalReadResult journal;
        try
        {
            using var deadline = new CancellationTokenSource(_transactionTimeout);
            journal = await _journalStore.ReadAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            lock (_statusLock)
            {
                if (_journalRecoveryState == JournalRecoveryStates.None)
                    _journalRecoveryState = JournalRecoveryStates.Unknown;
            }
            return false;
        }
        if (journal.Status == SwitchJournalReadStatus.Absent) return true;
        lock (_statusLock)
        {
            // Never clear established recovery state while retiring a transaction.
            if (_journalRecoveryState == JournalRecoveryStates.None)
                _journalRecoveryState = journal.Status switch
                {
                    SwitchJournalReadStatus.Valid => JournalRecoveryStates.ActionRequired,
                    SwitchJournalReadStatus.Corrupt or SwitchJournalReadStatus.UnsupportedVersion => JournalRecoveryStates.NotResolvable,
                    _ => JournalRecoveryStates.Unknown
                };
        }
        return false;
    }

    private async Task<WinCredEntry> CaptureCurrentCredentialAsync(CancellationToken cancellationToken)
    {
        var readCredential = await _winCredReader.ReadCredentialAsync(WinCredTarget, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SwitchRejectedException(
                SwitchResultCodes.TelemetryUnavailable,
                "Current credential is unavailable for rollback snapshotting.");
        try
        {
            ValidateCredential(readCredential, requireConfiguredTarget: true);
            return CloneCredential(readCredential);
        }
        finally
        {
            ZeroCredential(readCredential);
        }
    }

    private async Task VerifyIdentityAsync(
        string expectedEmail,
        AG2ProcessGeneration expectedGeneration,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _verificationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _processLifecycle.IsGenerationCurrentAsync(expectedGeneration, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException("Process generation changed during identity verification.");
            }

            var identity = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            if (!await _processLifecycle.IsGenerationCurrentAsync(expectedGeneration, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException("Telemetry came from a stale process generation.");
            }
            if (identity != null)
            {
                if (string.Equals(identity.Email, expectedEmail, StringComparison.OrdinalIgnoreCase)) return;
                throw new InvalidOperationException("Post-restart identity did not match the requested account.");
            }
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for verified account identity telemetry.");
    }

    private async Task RestoreCredentialConditionallyAsync(WinCredEntry original, WinCredEntry applied, CancellationToken cancellationToken)
    {
        var current = await _winCredReader.ReadCredentialAsync(WinCredTarget, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Credential disappeared during rollback.");
        try
        {
            if (CredentialsEqual(current, original)) return;
            if (!CredentialsEqual(current, applied))
            {
                throw new InvalidOperationException(
                    "Credential changed outside this switch attempt; refusing to overwrite newer state.");
            }
        }
        finally
        {
            ZeroCredential(current);
        }

        bool restored = await _winCredWriter.WriteCredentialAsync(original, cancellationToken)
            .ConfigureAwait(false);
        if (!restored) throw new InvalidOperationException("Credential rollback writer reported failure.");
        await RequireCredentialEqualsAsync(original, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireCredentialEqualsAsync(WinCredEntry expected, CancellationToken cancellationToken)
    {
        var actual = await _winCredReader.ReadCredentialAsync(WinCredTarget, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Credential read-back was missing.");
        try
        {
            if (!CredentialsEqual(actual, expected))
            {
                throw new InvalidOperationException("Credential read-back verification failed.");
            }
        }
        finally
        {
            ZeroCredential(actual);
        }
    }

    private static bool CredentialsEqual(WinCredEntry left, WinCredEntry right) =>
        string.Equals(left.Target, right.Target, StringComparison.OrdinalIgnoreCase) &&
        left.Type == right.Type &&
        string.Equals(left.UserName, right.UserName, StringComparison.Ordinal) &&
        left.Persistence == right.Persistence &&
        left.Blob.Length == right.Blob.Length &&
        CryptographicOperations.FixedTimeEquals(left.Blob, right.Blob);

    private static WinCredEntry CloneCredential(WinCredEntry source) =>
        new(source.Target, source.Type, source.UserName, source.Persistence, (byte[])source.Blob.Clone());

    private static void ValidateCredential(WinCredEntry entry, bool requireConfiguredTarget)
    {
        if (requireConfiguredTarget && !string.Equals(entry.Target, WinCredTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Credential target does not match the configured Antigravity target.");
        if (entry.Target.IndexOf('\0') >= 0 || entry.UserName?.IndexOf('\0') >= 0)
            throw new InvalidDataException("Credential text contains an embedded null.");
        if (string.IsNullOrWhiteSpace(entry.UserName) ||
            entry.UserName.Length > VaultConstants.MaxAg2WinCredUserNameLength)
            throw new InvalidDataException("Credential username is structurally invalid.");
        if (entry.Type != 1 || entry.Persistence is < 1 or > 3)
            throw new InvalidDataException("Credential type or persistence is unsupported.");
        if (entry.Blob == null || entry.Blob.Length is < 1 or > MaxCredentialBlobBytes)
            throw new InvalidDataException("Credential blob is empty or outside Windows bounds.");
    }

    private static void ZeroCredential(WinCredEntry? entry)
    {
        if (entry?.Blob is { Length: > 0 }) CryptographicOperations.ZeroMemory(entry.Blob);
    }

    private static bool SourceIdentityMatches(AccountMetadata? source, AccountIdentityDto? live) =>
        source != null && !string.IsNullOrWhiteSpace(source.Email) &&
        !string.IsNullOrWhiteSpace(live?.Email) &&
        string.Equals(source.Email.Trim(), live.Email.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string SafeFailure(Exception error) =>
        error is OperationCanceledException
            ? "Switch was cancelled during a protected phase."
            : AG2Security.SanitizeError(error);

    private void SetActive(string transactionId, string state)
    {
        lock (_statusLock)
        {
            _activeTransactionId = transactionId;
            if (!_recoveryQuarantine.IsMarked) _currentState = state;
        }
    }

    private void SetState(string state)
    {
        lock (_statusLock)
        {
            if (!_recoveryQuarantine.IsMarked) _currentState = state;
        }
    }

    private NativeSwitchResult Terminal(
        string? transactionId,
        bool success,
        string code,
        string state,
        string targetAccountId,
        string? targetEmail,
        string? previousAccountId,
        string? previousEmail,
        string message,
        IReadOnlyList<string> stages,
        string startedAt)
    {
        var result = new NativeSwitchResult(transactionId, success, code, state,
            targetAccountId, targetEmail, previousAccountId, previousEmail,
            AG2Security.RedactSensitiveText(message), stages.ToArray(), startedAt,
            DateTimeOffset.UtcNow.ToString("O"),
            ManualRecoveryRequired: code == SwitchResultCodes.SwitchFailedRollbackFailed);
        lock (_statusLock)
        {
            if (_recoveryQuarantine.IsMarked)
            {
                result = result with
                {
                    Success = false,
                    Code = SwitchResultCodes.SwitchFailedRollbackFailed,
                    State = NativeSwitchStates.Failed,
                    ManualRecoveryRequired = true,
                    Message = "Switch outcome remained quarantined; manual recovery is required."
                };
                state = NativeSwitchStates.Failed;
                if (_journalRecoveryState != JournalRecoveryStates.RestartRequired &&
                    _journalRecoveryState != JournalRecoveryStates.Unknown &&
                    _journalRecoveryState != JournalRecoveryStates.NotResolvable)
                {
                    _journalRecoveryState = JournalRecoveryStates.ActionRequired;
                }
            }
            _currentState = state;
            _lastResult = result;
        }
        return result;
    }

    private async Task WriteJournalStateAsync(
        string transactionId,
        SwitchJournalState state,
        string sourceAccountId,
        string targetAccountId,
        string? quarantineReasonCode = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new SwitchJournalEntry
        {
            Magic = SwitchJournalEntry.CurrentMagic,
            SchemaVersion = SwitchJournalEntry.CurrentSchemaVersion,
            TransactionId = transactionId,
            State = state,
            UpdatedAt = DateTimeOffset.UtcNow,
            SourceAccountId = sourceAccountId,
            TargetAccountId = targetAccountId,
            QuarantineReasonCode = quarantineReasonCode
        };

        await _journalStore.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<AccountModelQuotaObservation>> VerifyTargetQuotaAsync(
        AccountMetadata target,
        AG2ProcessGeneration? generation,
        string requiredWorkloadModelKey,
        double? minimumCandidateQuotaPercent,
        List<string> stages,
        CancellationToken cancellationToken)
    {
        string? canonicalKey = CandidateSelector.CanonicalizeModelKey(requiredWorkloadModelKey);
        if (string.IsNullOrWhiteSpace(canonicalKey))
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.InvalidWorkloadModelKey);
        }

        if (generation == null || !await _processLifecycle.IsGenerationCurrentAsync(generation, cancellationToken).ConfigureAwait(false))
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.ProcessGenerationChanged);
        }

        AccountQuotaObservation observation;
        try
        {
            observation = await _adapter.GetAccountQuotaObservationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.TelemetryUnavailable, innerException: ex);
        }

        if (generation == null || !await _processLifecycle.IsGenerationCurrentAsync(generation, cancellationToken).ConfigureAwait(false))
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.ProcessGenerationChanged);
        }

        if (observation == null || observation.Account == null ||
            string.IsNullOrWhiteSpace(observation.Account.Email) ||
            !string.Equals(observation.Account.Email.Trim(), target.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.TargetIdentityMismatch);
        }

        if (observation.Quota == null || observation.Quota.Models == null || observation.Quota.Models.Count == 0)
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.TelemetryUnavailable);
        }

        var matchingModels = observation.Quota.Models
            .Where(m => string.Equals(CandidateSelector.GetModelKey(m), canonicalKey, StringComparison.Ordinal))
            .ToList();

        if (matchingModels.Count == 0)
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.RequestedModelAbsent);
        }

        double minRequiredFraction = (minimumCandidateQuotaPercent ?? 0.0) / 100.0;
        foreach (var m in matchingModels)
        {
            if (m.IsExhausted)
            {
                throw new SwitchVerificationException(SwitchVerificationFailureReasons.RequestedModelExhausted);
            }

            if (!m.RemainingFraction.HasValue)
            {
                throw new SwitchVerificationException(SwitchVerificationFailureReasons.QuotaUnknown);
            }

            if (!double.IsFinite(m.RemainingFraction.Value) || m.RemainingFraction.Value < 0.0 || m.RemainingFraction.Value > 1.0)
            {
                throw new SwitchVerificationException(SwitchVerificationFailureReasons.QuotaInvalid);
            }

            if (m.RemainingFraction.Value < minRequiredFraction)
            {
                throw new SwitchVerificationException(SwitchVerificationFailureReasons.BelowMinimumThreshold);
            }
        }

        stages.Add("TARGET_QUOTA_VERIFIED");

        return QuotaObservationEvidence.Capture(target.Id, observation.Quota.Models,
            _timeProvider.GetUtcNow(), "LiveTargetVerification").ToList();
    }

    private sealed class SwitchRejectedException : Exception
    {
        public string Code { get; }
        public SwitchRejectedException(string code, string message) : base(message) => Code = code;
    }
}

public static class SwitchVerificationFailureReasons
{
    public const string InvalidWorkloadModelKey = "InvalidWorkloadModelKey";
    public const string ProcessGenerationChanged = "ProcessGenerationChanged";
    public const string TelemetryUnavailable = "TelemetryUnavailable";
    public const string TargetIdentityMismatch = "TargetIdentityMismatch";
    public const string RequestedModelAbsent = "RequestedModelAbsent";
    public const string RequestedModelExhausted = "RequestedModelExhausted";
    public const string QuotaUnknown = "QuotaUnknown";
    public const string QuotaInvalid = "QuotaInvalid";
    public const string BelowMinimumThreshold = "BelowMinimumThreshold";
}

public sealed class SwitchVerificationException : Exception
{
    public string Reason { get; }

    public SwitchVerificationException(string reason, string? message = null, Exception? innerException = null)
        : base(message ?? $"Target quota verification failed: {reason}.", innerException)
    {
        Reason = reason;
    }

    public SwitchVerificationException(string reason, Exception? innerException)
        : base($"Target quota verification failed: {reason}.", innerException)
    {
        Reason = reason;
    }
}
