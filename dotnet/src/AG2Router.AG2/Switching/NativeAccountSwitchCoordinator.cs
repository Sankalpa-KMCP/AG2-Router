using System.Security.Cryptography;
using AG2Router.AG2.Persistence;
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
    private readonly object _statusLock = new();

    private string _currentState = NativeSwitchStates.Idle;
    private string? _activeTransactionId;
    private NativeSwitchResult? _lastResult;

    public NativeAccountSwitchCoordinator(
        IAccountStore accountStore,
        SessionVault sessionVault,
        IWinCredReader winCredReader,
        IWinCredWriter winCredWriter,
        IAG2Adapter adapter,
        IAG2ProcessLifecycle processLifecycle,
        TimeSpan? processTimeout = null,
        TimeSpan? verificationTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        _sessionVault = sessionVault ?? throw new ArgumentNullException(nameof(sessionVault));
        _winCredReader = winCredReader ?? throw new ArgumentNullException(nameof(winCredReader));
        _winCredWriter = winCredWriter ?? throw new ArgumentNullException(nameof(winCredWriter));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _processLifecycle = processLifecycle ?? throw new ArgumentNullException(nameof(processLifecycle));
        _processTimeout = processTimeout ?? TimeSpan.FromSeconds(10);
        _verificationTimeout = verificationTimeout ?? TimeSpan.FromSeconds(20);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
    }

    public NativeSwitchStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new NativeSwitchStatus(_activeTransactionId, _currentState, _lastResult);
        }
    }

    public async Task<NativeSwitchResult> SwitchAsync(
        string targetAccountId,
        CancellationToken cancellationToken = default)
    {
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

        string transactionId = $"tx_{Guid.NewGuid():N}";
        var stages = new List<string>();
        AccountMetadata? target = null;
        string? previousAccountId = null;
        string? previousEmail = null;
        WinCredEntry? originalCredential = null;
        WinCredEntry? targetCredential = null;
        byte[]? targetSession = null;
        AG2ProcessSnapshot? processSnapshot = null;
        AG2ProcessGeneration? replacementGeneration = null;
        bool credentialWriteAttempted = false;

        SetActive(transactionId, NativeSwitchStates.Preflight);
        try
        {
            await using var lease = await CrossProcessFileLease
                .AcquireAsync(_sessionVault.GetVaultPath() + ".switch", cancellationToken)
                .ConfigureAwait(false);

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
            var currentIdentity = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            previousEmail = currentIdentity?.Email;
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

            var readCredential = await _winCredReader.ReadCredentialAsync(WinCredTarget, cancellationToken)
                .ConfigureAwait(false);
            if (readCredential == null)
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TelemetryUnavailable,
                    "Current credential is unavailable for rollback snapshotting.");
            }
            try
            {
                ValidateCredential(readCredential, requireConfiguredTarget: true);
                originalCredential = CloneCredential(readCredential);
            }
            finally
            {
                ZeroCredential(readCredential);
            }
            stages.Add("ROLLBACK_SNAPSHOT_CAPTURED");

            // Re-check both process generation and idle telemetry immediately before mutation.
            await _processLifecycle.RevalidateAsync(processSnapshot, cancellationToken).ConfigureAwait(false);
            var finalActivity = await _adapter.GetActivityStateAsync(cancellationToken).ConfigureAwait(false);
            await _processLifecycle.RevalidateAsync(processSnapshot, cancellationToken).ConfigureAwait(false);
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

            targetSession = await _sessionVault.GetSessionAsync(target.Id, cancellationToken).ConfigureAwait(false);
            if (targetSession == null || targetSession.Length == 0 || targetSession.Length > MaxCredentialBlobBytes)
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TargetNotVaulted,
                    "Target vaulted session is missing, empty, or outside credential bounds.");
            }
            targetCredential = new WinCredEntry(WinCredTarget, 1, target.Email, 2, targetSession);
            ValidateCredential(targetCredential, requireConfiguredTarget: true);

            SetState(NativeSwitchStates.ApplyingCredential);
            credentialWriteAttempted = true;
            bool written = await _winCredWriter.WriteCredentialAsync(targetCredential, cancellationToken)
                .ConfigureAwait(false);
            if (!written) throw new InvalidOperationException("Credential writer reported failure.");
            await RequireCredentialEqualsAsync(targetCredential, cancellationToken).ConfigureAwait(false);
            stages.Add("CREDENTIAL_APPLIED_AND_VERIFIED");

            SetState(NativeSwitchStates.StoppingProcess);
            await _processLifecycle.StopVerifiedAsync(processSnapshot, _processTimeout, cancellationToken)
                .ConfigureAwait(false);
            stages.Add("SOURCE_PROCESS_STOPPED");

            SetState(NativeSwitchStates.Restarting);
            int launchedPid = await _processLifecycle.LaunchAsync(processSnapshot, cancellationToken)
                .ConfigureAwait(false);
            replacementGeneration = await _processLifecycle.WaitForHealthyReplacementAsync(
                processSnapshot,
                launchedPid,
                _verificationTimeout,
                cancellationToken).ConfigureAwait(false);
            stages.Add("REPLACEMENT_PROCESS_VERIFIED");

            SetState(NativeSwitchStates.Verifying);
            await VerifyIdentityAsync(target.Email, replacementGeneration, cancellationToken).ConfigureAwait(false);
            stages.Add("TARGET_IDENTITY_VERIFIED");

            SetState(NativeSwitchStates.Finalizing);
            cancellationToken.ThrowIfCancellationRequested();
            var updated = await _accountStore.UpdateAccountAsync(target.Id, new UpdateAccountInput(
                ValidationStatus: AccountValidationStatus.Valid,
                HasVaultedSession: true,
                LastActiveAt: DateTimeOffset.UtcNow.ToString("O")), cancellationToken).ConfigureAwait(false);
            if (updated == null) throw new InvalidOperationException("Target metadata disappeared during switch finalization.");
            bool activeCommitted = await _accountStore.CompareExchangeActiveAccountIdAsync(
                previousAccountId,
                target.Id,
                CancellationToken.None).ConfigureAwait(false);
            if (!activeCommitted) throw new InvalidOperationException("Active-account metadata changed during switch finalization.");
            stages.Add("METADATA_COMMITTED");

            return Terminal(transactionId, true, SwitchResultCodes.Success, NativeSwitchStates.Complete,
                target.Id, target.Email, previousAccountId, previousEmail,
                "Target account was activated and verified.", stages, now);
        }
        catch (SwitchRejectedException ex) when (!credentialWriteAttempted)
        {
            return Terminal(transactionId, false, ex.Code, NativeSwitchStates.Failed,
                requestedId, target?.Email, previousAccountId, previousEmail,
                AG2Security.RedactSensitiveText(ex.Message), stages, now);
        }
        catch (OperationCanceledException) when (!credentialWriteAttempted)
        {
            return Terminal(transactionId, false, SwitchResultCodes.Cancelled, NativeSwitchStates.Failed,
                requestedId, target?.Email, previousAccountId, previousEmail,
                "Switch request was cancelled before credential mutation.", stages, now);
        }
        catch (Exception switchError)
        {
            if (!credentialWriteAttempted || originalCredential == null || targetCredential == null || processSnapshot == null)
            {
                return Terminal(transactionId, false,
                    switchError is OperationCanceledException ? SwitchResultCodes.Cancelled : SwitchResultCodes.TelemetryUnavailable,
                    NativeSwitchStates.Failed, requestedId, target?.Email, previousAccountId, previousEmail,
                    SafeFailure(switchError), stages, now);
            }

            SetState(NativeSwitchStates.RollingBack);
            stages.Add("ROLLBACK_STARTED");
            try
            {
                await RestoreCredentialConditionallyAsync(originalCredential, targetCredential)
                    .ConfigureAwait(false);
                stages.Add("ORIGINAL_CREDENTIAL_RESTORED");

                var restoredGeneration = await _processLifecycle.RestoreAsync(
                    processSnapshot,
                    _verificationTimeout,
                    CancellationToken.None).ConfigureAwait(false);
                stages.Add("SOURCE_PROCESS_RESTORED");

                if (string.IsNullOrWhiteSpace(previousEmail))
                {
                    throw new InvalidOperationException("Original identity was unavailable for rollback verification.");
                }
                await VerifyIdentityAsync(previousEmail, restoredGeneration, CancellationToken.None)
                    .ConfigureAwait(false);
                stages.Add("SOURCE_IDENTITY_VERIFIED");

                return Terminal(transactionId, false, SwitchResultCodes.SwitchFailedRolledBack,
                    NativeSwitchStates.RolledBack, requestedId, target?.Email, previousAccountId, previousEmail,
                    "Switch failed; the original credential and verified source identity were restored.", stages, now);
            }
            catch (Exception rollbackError)
            {
                stages.Add("ROLLBACK_FAILED");
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
            lock (_statusLock) { _activeTransactionId = null; }
            SwitchGate.Release();
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

    private async Task RestoreCredentialConditionallyAsync(WinCredEntry original, WinCredEntry applied)
    {
        var current = await _winCredReader.ReadCredentialAsync(WinCredTarget, CancellationToken.None)
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

        bool restored = await _winCredWriter.WriteCredentialAsync(original, CancellationToken.None)
            .ConfigureAwait(false);
        if (!restored) throw new InvalidOperationException("Credential rollback writer reported failure.");
        await RequireCredentialEqualsAsync(original, CancellationToken.None).ConfigureAwait(false);
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
        if (entry.Target.IndexOf('\0') >= 0 || entry.UserName.IndexOf('\0') >= 0)
            throw new InvalidDataException("Credential text contains an embedded null.");
        if (entry.Type != 1 || entry.Persistence is < 1 or > 3)
            throw new InvalidDataException("Credential type or persistence is unsupported.");
        if (entry.Blob == null || entry.Blob.Length is < 1 or > MaxCredentialBlobBytes)
            throw new InvalidDataException("Credential blob is empty or outside Windows bounds.");
    }

    private static void ZeroCredential(WinCredEntry? entry)
    {
        if (entry?.Blob is { Length: > 0 }) CryptographicOperations.ZeroMemory(entry.Blob);
    }

    private static string SafeFailure(Exception error) =>
        error is OperationCanceledException
            ? "Switch was cancelled during a protected phase."
            : AG2Security.SanitizeError(error);

    private void SetActive(string transactionId, string state)
    {
        lock (_statusLock)
        {
            _activeTransactionId = transactionId;
            _currentState = state;
        }
    }

    private void SetState(string state)
    {
        lock (_statusLock) { _currentState = state; }
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
            DateTimeOffset.UtcNow.ToString("O"));
        lock (_statusLock)
        {
            _currentState = state;
            _lastResult = result;
        }
        return result;
    }

    private sealed class SwitchRejectedException : Exception
    {
        public string Code { get; }
        public SwitchRejectedException(string code, string message) : base(message) => Code = code;
    }
}
