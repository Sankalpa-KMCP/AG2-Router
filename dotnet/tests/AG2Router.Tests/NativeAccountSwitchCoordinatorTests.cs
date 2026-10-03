using System.Collections.Concurrent;
using System.IO;
using System.Text;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Switching;
using AG2Router.AG2.Vault;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

[Collection("SwitchCoordinator")]
public sealed class NativeAccountSwitchCoordinatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ag2_switch_{Guid.NewGuid():N}");
    private readonly InMemoryAccountStore _accounts = new();
    private readonly FakeDpapiProvider _dpapi = new();
    private readonly SwitchCredentialStore _credentials = new();
    private readonly SwitchAdapter _adapter = new();
    private readonly SwitchProcessLifecycle _process = new();
    private readonly SessionVault _vault;
    private AccountMetadata? _source;
    private AccountMetadata? _target;

    public NativeAccountSwitchCoordinatorTests()
    {
        Directory.CreateDirectory(_tempDir);
        _vault = new SessionVault(_tempDir, _dpapi);
        _credentials.Set(Credential("source@example.com", "source-secret"));
        _adapter.Identity = new AccountIdentityDto("source@example.com");
        _process.OnReplacement = () => _adapter.Identity = new AccountIdentityDto("target@example.com");
        _process.OnRestore = () => _adapter.Identity = new AccountIdentityDto("source@example.com");
    }

    public void Dispose()
    {
        _credentials.Clear();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task MissingTargetFailsBeforeMutation()
    {
        var result = await Coordinator().SwitchAsync("missing");
        Assert.Equal(SwitchResultCodes.TargetNotFound, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
    }

    [Fact]
    public async Task MissingVaultFailsBeforeMutation()
    {
        await SeedAccountsAsync(vaultTarget: false);
        var result = await Coordinator().SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.TargetNotVaulted, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Fact]
    public async Task AlreadyActiveFailsBeforeMutation()
    {
        await SeedAccountsAsync();
        await _accounts.SetActiveAccountIdAsync(_target!.Id);
        _adapter.Identity = new AccountIdentityDto(_target.Email);
        var result = await Coordinator().SwitchAsync(_target.Id);
        Assert.Equal(SwitchResultCodes.AlreadyActive, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Fact]
    public async Task StoredSourceAndLiveIdentityMismatchFailsBeforeMutation()
    {
        await SeedAccountsAsync();
        _adapter.Identity = new AccountIdentityDto("external@example.com");

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.Equal(SwitchResultCodes.TelemetryUnavailable, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task SourceIdentityRotationBeforeMutationFailsClosed()
    {
        await SeedAccountsAsync();
        int reads = 0;
        _adapter.IdentityBehavior = _ => Task.FromResult<AccountIdentityDto?>(
            new AccountIdentityDto(Interlocked.Increment(ref reads) == 1
                ? "source@example.com" : "external@example.com"));

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.Equal(SwitchResultCodes.TelemetryUnavailable, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
    }

    [Fact]
    public async Task IdentityChangesWhileTargetVaultLoads_NoProcessOrCredentialMutation()
    {
        await SeedAccountsAsync();
        var decryptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeDecrypt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingVault = new SessionVault(_tempDir, new DelayedDecryptProvider(_dpapi,
            decryptEntered, resumeDecrypt));
        var coordinator = new NativeAccountSwitchCoordinator(_accounts, blockingVault,
            _credentials, _credentials, _adapter, _process,
            new SwitchJournalStore(Path.Combine(_tempDir, "switch-journal.json")));

        var switching = coordinator.SwitchAsync(_target!.Id);
        await decryptEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _adapter.Identity = new AccountIdentityDto("external@example.com");
        resumeDecrypt.TrySetResult();
        var result = await switching.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SwitchResultCodes.TelemetryUnavailable, result.Code);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Fact]
    public async Task IdentityChangesAtLastPreStopRead_NoProcessOrCredentialMutation()
    {
        await SeedAccountsAsync();
        int reads = 0;
        _adapter.IdentityBehavior = _ => Task.FromResult<AccountIdentityDto?>(
            new AccountIdentityDto(Interlocked.Increment(ref reads) >= 3
                ? "external@example.com" : "source@example.com"));

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.Equal(SwitchResultCodes.TelemetryUnavailable, result.Code);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Fact]
    public async Task IdentityChangesDuringFinalActiveRead_NoProcessOrCredentialMutation()
    {
        await SeedAccountsAsync();
        int reads = 0;
        _adapter.IdentityBehavior = _ => Task.FromResult<AccountIdentityDto?>(
            new AccountIdentityDto(Interlocked.Increment(ref reads) >= 4
                ? "external@example.com" : "source@example.com"));

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.Equal(SwitchResultCodes.TelemetryUnavailable, result.Code);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Fact]
    public async Task IdentityChangesDuringStopRevalidation_NoProcessOrCredentialMutation()
    {
        await SeedAccountsAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.BeforeKillBehavior = async _ =>
        {
            entered.TrySetResult();
            await resume.Task;
        };

        var switching = Coordinator().SwitchAsync(_target!.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _adapter.Identity = new AccountIdentityDto("external@example.com");
        resume.TrySetResult();
        var result = await switching.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SwitchResultCodes.TelemetryUnavailable, result.Code);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Theory]
    [InlineData("BUSY", 1, SwitchResultCodes.Ag2Busy)]
    [InlineData("UNKNOWN", 0, SwitchResultCodes.TelemetryUnavailable)]
    public async Task ActivityChangesAtStopBoundary_NoProcessOrCredentialMutation(
        string state, int running, string expectedCode)
    {
        await SeedAccountsAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.BeforeKillBehavior = async _ =>
        {
            entered.TrySetResult();
            await resume.Task;
        };

        var switching = Coordinator().SwitchAsync(_target!.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _adapter.Activity = new ActivityStatusDto(state, running, running, DateTimeOffset.UtcNow.ToString("O"));
        resume.TrySetResult();
        var result = await switching.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Fact]
    public async Task ThrowingFinalActivityProofNeverQuiescesOriginalProcess()
    {
        await SeedAccountsAsync();
        _process.BeforeKillBehavior = _ =>
        {
            _adapter.ActivityBehavior = _ => throw new IOException("synthetic final activity failure");
            return Task.CompletedTask;
        };

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.Equal(SwitchResultCodes.TelemetryUnavailable, result.Code);
        Assert.False(result.Success);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _process.QuiesceCount);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Fact]
    public async Task MissingActivityEvidenceFailsBeforeMutation()
    {
        await SeedAccountsAsync();
        _adapter.Activity = AG2Router.AG2.Normalization.AG2TelemetryNormalizer.NormalizeActivitySnapshot(null);

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.Equal(SwitchResultCodes.TelemetryUnavailable, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
    }

    [Theory]
    [InlineData("BUSY", 1, SwitchResultCodes.Ag2Busy)]
    [InlineData("UNKNOWN", 0, SwitchResultCodes.TelemetryUnavailable)]
    public async Task NonIdleActivityFailsClosed(string state, int running, string expected)
    {
        await SeedAccountsAsync();
        _adapter.Activity = new ActivityStatusDto(state, running, running, DateTimeOffset.UtcNow.ToString("O"));
        var result = await Coordinator().SwitchAsync(_target!.Id);
        Assert.Equal(expected, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Theory]
    [InlineData("DEGRADED")]
    [InlineData("OFFLINE")]
    [InlineData("ERROR")]
    public async Task UnavailableTelemetryFailsClosed(string status)
    {
        await SeedAccountsAsync();
        _adapter.Status = new Ag2StatusDto(false, status, _adapter.Activity, "synthetic");
        var result = await Coordinator().SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.TelemetryUnavailable, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Fact]
    public async Task AmbiguousOrUnverifiedProcessFailsBeforeCredentialWrite()
    {
        await SeedAccountsAsync();
        _process.CaptureError = new AG2ProcessLifecycleException("ambiguous synthetic processes");
        var result = await Coordinator().SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.UnsafeProcess, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Fact]
    public async Task SuccessfulSyntheticSwitchVerifiesIdentityThenCommitsMetadata()
    {
        await SeedAccountsAsync();
        _credentials.Set(Credential(VaultConstants.DefaultAg2WinCredUserName, "source-secret"));
        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.True(result.Success);
        Assert.Equal(SwitchResultCodes.Success, result.Code);
        Assert.Equal(_target.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.True((await _accounts.GetAccountAsync(_target.Id))!.HasVaultedSession);
        Assert.Equal("target@example.com", _adapter.Identity!.Email);
        Assert.Equal("antigravity", _credentials.Snapshot().UserName);
        Assert.Equal(1, _process.StopCount);
        Assert.Equal(1, _process.LaunchCount);
        Assert.Contains("TARGET_IDENTITY_VERIFIED", result.StagesCompleted);
        Assert.True(result.StagesCompleted.IndexOf("TARGET_IDENTITY_VERIFIED") <
                    result.StagesCompleted.IndexOf("METADATA_COMMITTED"));
    }

    [Fact]
    public async Task LegacyUsernameSwitchesAndForwardWriteNormalizesUsername()
    {
        await SeedAccountsAsync();
        _credentials.Set(Credential("legacy@example.com", "source-secret"));

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.True(result.Success);
        Assert.Equal(VaultConstants.DefaultAg2WinCredUserName, _credentials.Snapshot().UserName);
        Assert.Equal(_target.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task UsernameAtWindowsLengthLimitPassesSwitchPreflight()
    {
        await SeedAccountsAsync();
        _credentials.Set(Credential(new string('x', 513), "source-secret"));

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.True(result.Success);
        Assert.Equal("antigravity", _credentials.Snapshot().UserName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0name")]
    public async Task StructurallyInvalidSourceUsernameFailsBeforeMutation(string username)
    {
        await SeedAccountsAsync();
        _credentials.Set(Credential(username, "source-secret"));

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
    }

    [Fact]
    public async Task OversizedSourceUsernameFailsBeforeMutation()
    {
        await SeedAccountsAsync();
        _credentials.Set(Credential(new string('x', 514),
            "source-secret"));

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("stop")]
    [InlineData("launch")]
    [InlineData("reconnect")]
    [InlineData("wrong_identity")]
    [InlineData("identity_timeout")]
    public async Task PostMutationFailuresRollbackCredentialProcessAndIdentity(string failure)
    {
        await SeedAccountsAsync();
        var originalCredential = _credentials.Snapshot();
        Assert.Equal("source@example.com", originalCredential.UserName);
        ConfigureFailure(failure);

        var result = await Coordinator(TimeSpan.FromMilliseconds(40)).SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        var restoredCredential = _credentials.Snapshot();
        Assert.Equal(originalCredential.Target, restoredCredential.Target);
        Assert.Equal(originalCredential.Type, restoredCredential.Type);
        Assert.Equal(originalCredential.UserName, restoredCredential.UserName);
        Assert.Equal(originalCredential.Persistence, restoredCredential.Persistence);
        Assert.Equal(originalCredential.Blob, restoredCredential.Blob);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Equal("source@example.com", _adapter.Identity!.Email);
        Assert.True(_process.RestoreCount >= 1);
        Assert.Contains("SOURCE_IDENTITY_VERIFIED", result.StagesCompleted);
    }

    [Fact]
    public async Task RollbackCredentialWriteFailureIsExplicitManualRecovery()
    {
        await SeedAccountsAsync();
        _process.WaitError = new AG2ProcessLifecycleException("synthetic reconnect failure");
        _credentials.WriteBehavior = (call, entry, _) =>
        {
            if (call == 1) { _credentials.Set(entry); return Task.FromResult(true); }
            throw new InvalidOperationException("synthetic rollback write failure");
        };

        var result = await Coordinator().SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.False(result.Success);
        Assert.Contains("manual recovery", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(_target.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task RollbackIdentityVerificationFailureIsExplicit()
    {
        await SeedAccountsAsync();
        _process.StopError = new AG2ProcessLifecycleException("synthetic stop failure");
        _process.OnRestore = () => _adapter.Identity = new AccountIdentityDto("wrong-source@example.com");
        var result = await Coordinator(TimeSpan.FromMilliseconds(30)).SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.Contains("ROLLBACK_FAILED", result.StagesCompleted);
    }

    [Fact]
    public async Task RollbackProcessRestoreFailureIsExplicit()
    {
        await SeedAccountsAsync();
        _process.StopError = new AG2ProcessLifecycleException("synthetic stop failure");
        _process.RestoreError = new AG2ProcessLifecycleException("synthetic restore failure");
        var result = await Coordinator().SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
    }

    [Fact]
    public async Task FinalMetadataCasConflictPreservesNewerActiveSelectionAndRollsBackRuntime()
    {
        await SeedAccountsAsync();
        var third = await _accounts.AddAccountAsync(new CreateAccountInput(Email: "third@example.com"));
        _process.OnReplacement = () =>
        {
            _adapter.Identity = new AccountIdentityDto("target@example.com");
            _accounts.SetActiveAccountIdAsync(third.Id).GetAwaiter().GetResult();
        };

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Equal(third.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Null((await _accounts.GetAccountAsync(_target.Id))!.LastActiveAt);
        Assert.Equal("source@example.com", _adapter.Identity!.Email);
        Assert.Equal("source-secret", Encoding.UTF8.GetString(_credentials.Snapshot().Blob));
    }

    [Fact]
    public async Task ConcurrentRequestsNeverOverlapCredentialMutation()
    {
        await SeedAccountsAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _credentials.WriteBehavior = async (call, entry, ct) =>
        {
            if (call == 1)
            {
                _credentials.Set(entry);
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return true;
            }
            _credentials.Set(entry);
            return true;
        };

        Task<NativeSwitchResult> first = Coordinator().SwitchAsync(_target!.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await Coordinator().SwitchAsync(_target.Id);
        Assert.Equal(SwitchResultCodes.SwitchInProgress, second.Code);
        Assert.Equal(1, _credentials.WriteCount);
        release.TrySetResult();
        Assert.True((await first).Success);
    }

    [Fact]
    public async Task CancellationBeforeMutationMakesNoChanges()
    {
        await SeedAccountsAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await Coordinator().SwitchAsync(_target!.Id, cts.Token);
        Assert.Equal(SwitchResultCodes.Cancelled, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task CancellationDuringUncertainCredentialWriteStillRollsBack()
    {
        await SeedAccountsAsync();
        using var cts = new CancellationTokenSource();
        _credentials.WriteBehavior = (call, entry, _) =>
        {
            _credentials.Set(entry);
            if (call == 1)
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
            return Task.FromResult(true);
        };
        var result = await Coordinator().SwitchAsync(_target!.Id, cts.Token);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Equal("source-secret", Encoding.UTF8.GetString(_credentials.Snapshot().Blob));
        Assert.All(_credentials.WriteTokenWasCancelled.Skip(1), Assert.False);
        Assert.False(_process.RollbackTokenWasCancelled);
    }

    [Fact]
    public async Task StaleProcessGenerationBeforeMutationIsRejected()
    {
        await SeedAccountsAsync();
        _process.RevalidateErrorOnCall = 2;
        var result = await Coordinator().SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.UnsafeProcess, result.Code);
        Assert.Equal(0, _credentials.WriteCount);
    }

    [Fact]
    public async Task StaleGenerationCannotFalselyVerifyTarget()
    {
        await SeedAccountsAsync();
        _process.GenerationCurrent = false;
        var result = await Coordinator().SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.NotEqual(_target.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task InFlightOldGenerationIdentityCannotFinalizeSwitch()
    {
        await SeedAccountsAsync();
        var identityEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIdentity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        _adapter.IdentityBehavior = async ct =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call <= 5)
                return new AccountIdentityDto("source@example.com");
            if (call > 6) return new AccountIdentityDto("source@example.com");
            identityEntered.TrySetResult();
            await releaseIdentity.Task.WaitAsync(ct);
            return new AccountIdentityDto("target@example.com");
        };

        Task<NativeSwitchResult> pending = Coordinator().SwitchAsync(_target!.Id);
        await identityEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _process.GenerationCurrent = false;
        releaseIdentity.TrySetResult();

        var result = await pending;
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task ExternalCredentialChangeIsNeverOverwrittenByRollback()
    {
        await SeedAccountsAsync();
        _credentials.WriteBehavior = (call, entry, _) =>
        {
            if (call == 1)
            {
                _credentials.Set(Credential("external@example.com", "external-newer-secret"));
                return Task.FromResult(true);
            }
            _credentials.Set(entry);
            return Task.FromResult(true);
        };
        var result = await Coordinator().SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.Equal("external-newer-secret", Encoding.UTF8.GetString(_credentials.Snapshot().Blob));
    }

    [Fact]
    public async Task RollbackSnapshotRefreshesAfterSourceProcessIsQuiesced()
    {
        await SeedAccountsAsync();
        _process.OnStop = () =>
            _credentials.Set(Credential("source@example.com", "source-refreshed-before-stop"));
        _process.WaitError = new AG2ProcessLifecycleException("synthetic reconnect failure");

        var result = await Coordinator().SwitchAsync(_target!.Id);

        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Equal("source-refreshed-before-stop", Encoding.UTF8.GetString(_credentials.Snapshot().Blob));
        Assert.Contains("ROLLBACK_SNAPSHOT_REFRESHED_AFTER_QUIESCE", result.StagesCompleted);
    }

    [Fact]
    public async Task SensitiveDependencyErrorsAreRedactedFromResultAndStatus()
    {
        await SeedAccountsAsync();
        _process.StopError = new AG2ProcessLifecycleException(
            "--csrf_token supersecret --host_bridge_token=bridgevalue --password hunter2");
        _process.RestoreError = new AG2ProcessLifecycleException("token=rollbacksecret");
        var coordinator = Coordinator();
        var result = await coordinator.SwitchAsync(_target!.Id);
        string exposed = System.Text.Json.JsonSerializer.Serialize(new { result, status = coordinator.GetStatus() });
        Assert.DoesNotContain("supersecret", exposed);
        Assert.DoesNotContain("bridgevalue", exposed);
        Assert.DoesNotContain("hunter2", exposed);
        Assert.DoesNotContain("rollbacksecret", exposed);
    }

    [Fact]
    public async Task CoordinateShutdownAsync_WhenIdle_CompletesAndRejectsSubsequentSwitches()
    {
        await SeedAccountsAsync();
        var coordinator = Coordinator();
        await coordinator.CoordinateShutdownAsync(TimeSpan.FromSeconds(5));

        var result = await coordinator.SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.Cancelled, result.Code);
        Assert.Contains("shutting down", result.Message);
    }

    [Fact]
    public async Task CoordinateShutdownAsync_ClosesAdmissionAndReportsActiveTimeout()
    {
        await SeedAccountsAsync();
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.StopBehavior = async _ =>
        {
            stopped.TrySetResult(true);
            await release.Task;
        };
        var coordinator = Coordinator();
        var switching = coordinator.SwitchAsync(_target!.Id);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            coordinator.CoordinateShutdownAsync(TimeSpan.FromMilliseconds(20)));
        release.TrySetResult(true);
        var outcome = await switching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, outcome.Code);
        Assert.Equal(SwitchResultCodes.Cancelled, (await coordinator.SwitchAsync(_target.Id)).Code);
    }

    [Fact]
    public async Task CallerCancellationAfterProcessTransitionStarted_DoesNotCancelMutationOrRollback()
    {
        await SeedAccountsAsync();
        using var callerCts = new CancellationTokenSource();
        _process.OnStop = () =>
        {
            // Cancel caller token right after process transition starts
            callerCts.Cancel();
        };

        var coordinator = Coordinator();
        var result = await coordinator.SwitchAsync(_target!.Id, callerCts.Token);

        // Switch completed successfully despite caller cancellation after mutation boundary
        Assert.True(result.Success);
        Assert.Equal(SwitchResultCodes.Success, result.Code);
        Assert.Equal(_target!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task RollbackTimeoutProducesExplicitManualRecoveryState()
    {
        await SeedAccountsAsync();
        _process.StopError = new AG2ProcessLifecycleException("synthetic stop failure");
        _process.QuiesceBehavior = token => Task.Delay(Timeout.Infinite, token);
        var coordinator = new NativeAccountSwitchCoordinator(
            _accounts, _vault, _credentials, _credentials, _adapter, _process,
            new SwitchJournalStore(Path.Combine(_tempDir, "switch-journal.json")),
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(100),
            rollbackTimeout: TimeSpan.FromMilliseconds(30));

        var result = await coordinator.SwitchAsync(_target!.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Contains("ROLLBACK_FAILED", result.StagesCompleted);
    }

    [Fact]
    public async Task NonCooperativeRollbackTimesOutWithoutReleasingSwitchOwnership()
    {
        await SeedAccountsAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.StopError = new AG2ProcessLifecycleException("synthetic stop failure");
        _process.QuiesceBehavior = async _ =>
        {
            entered.TrySetResult();
            await release.Task; // deliberately ignores cancellation
        };
        var coordinator = Coordinator(transactionTimeout: TimeSpan.FromSeconds(2));
        try
        {
            var first = coordinator.SwitchAsync(_target!.Id);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var uncertain = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(uncertain.ManualRecoveryRequired);
            Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, uncertain.Code);

            var later = await Coordinator().SwitchAsync(_target.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(later.ManualRecoveryRequired);
            Assert.Equal(1, _process.StopCount);
            Assert.Equal(0, _credentials.WriteCount);
            Assert.True(coordinator.GetStatus().LastResult?.ManualRecoveryRequired);
        }
        finally
        {
            release.TrySetResult();
        }
        var gate = PathLockRegistry.Get(_vault.GetVaultPath() + ".switch");
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
        gate.Release();
        Assert.True(coordinator.GetStatus().LastResult?.ManualRecoveryRequired);
    }

    [Fact]
    public async Task NonCooperativeForwardMutationCannotRaceLaterSwitchOrDowngradeRecovery()
    {
        await SeedAccountsAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.StopBehavior = async _ =>
        {
            entered.TrySetResult();
            await release.Task; // late continuation can attempt its next mutation
        };
        var coordinator = Coordinator(transactionTimeout: TimeSpan.FromSeconds(2));
        try
        {
            var first = coordinator.SwitchAsync(_target!.Id);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var uncertain = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(uncertain.ManualRecoveryRequired);

            var later = await Coordinator().SwitchAsync(_target.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(later.ManualRecoveryRequired);
            Assert.Equal(1, _process.StopCount);
            Assert.Equal(0, _credentials.WriteCount);
        }
        finally
        {
            release.TrySetResult();
        }
        var gate = PathLockRegistry.Get(_vault.GetVaultPath() + ".switch");
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
        gate.Release();
        Assert.True(coordinator.GetStatus().LastResult?.ManualRecoveryRequired);
    }

    [Fact]
    public async Task LateCredentialWriteAfterDeadlineCannotOverlapAnotherTransaction()
    {
        await SeedAccountsAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _credentials.WriteBehavior = async (call, entry, _) =>
        {
            if (call == 1)
            {
                entered.TrySetResult();
                await release.Task; // the writer ignores cancellation, then really mutates
            }
            _credentials.Set(entry);
            return true;
        };
        var coordinator = Coordinator(transactionTimeout: TimeSpan.FromSeconds(2));
        try
        {
            var first = coordinator.SwitchAsync(_target!.Id);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var uncertain = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(uncertain.ManualRecoveryRequired);

            var later = await Coordinator().SwitchAsync(_target.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(later.ManualRecoveryRequired);
            Assert.Equal(1, _credentials.WriteCount);
            var gate = PathLockRegistry.Get(_vault.GetVaultPath() + ".switch");
            Assert.False(await gate.WaitAsync(TimeSpan.FromMilliseconds(30)));
        }
        finally
        {
            release.TrySetResult();
        }
        var settledGate = PathLockRegistry.Get(_vault.GetVaultPath() + ".switch");
        Assert.True(await settledGate.WaitAsync(TimeSpan.FromSeconds(5)));
        settledGate.Release();
        Assert.True(coordinator.GetStatus().LastResult?.ManualRecoveryRequired);
    }

    [Fact]
    public async Task CleanupFaultCannotMaskEstablishedRollbackFailure()
    {
        await SeedAccountsAsync();
        _process.StopError = new AG2ProcessLifecycleException("synthetic stop failure");
        _process.RestoreError = new AG2ProcessLifecycleException("synthetic restore failure");
        var coordinator = Coordinator();
        coordinator.BeforeLeaseReleaseAsync = () => throw new IOException("synthetic lease release failure");

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.True(result.ManualRecoveryRequired);
        Assert.True(coordinator.GetStatus().LastResult?.ManualRecoveryRequired);
        Assert.Contains("Cleanup also failed", coordinator.GetStatus().LastResult?.Message);
    }

    private sealed class DelayedDecryptProvider(
        IDpapiProvider inner, TaskCompletionSource entered, TaskCompletionSource release) : IDpapiProvider
    {
        public Task<byte[]> EncryptAsync(byte[] plaintext, CancellationToken cancellationToken = default) =>
            inner.EncryptAsync(plaintext, cancellationToken);

        public async Task<byte[]> DecryptAsync(byte[] ciphertext, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return await inner.DecryptAsync(ciphertext, cancellationToken);
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
            await _vault.SaveSessionAsync(_target.Id, Encoding.UTF8.GetBytes("target-secret"));
        }
    }

    private NativeAccountSwitchCoordinator Coordinator(
        TimeSpan? verificationTimeout = null, TimeSpan? transactionTimeout = null) =>
        new(_accounts, _vault, _credentials, _credentials, _adapter, _process,
            new SwitchJournalStore(Path.Combine(_tempDir, "switch-journal.json")),
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: verificationTimeout ?? TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(5),
            transactionTimeout: transactionTimeout);

    private void ConfigureFailure(string failure)
    {
        switch (failure)
        {
            case "credential":
                _credentials.WriteBehavior = (call, entry, _) =>
                {
                    _credentials.Set(entry);
                    if (call == 1) throw new InvalidOperationException("synthetic uncertain write failure");
                    return Task.FromResult(true);
                };
                break;
            case "stop": _process.StopError = new AG2ProcessLifecycleException("stop failed"); break;
            case "launch": _process.LaunchError = new AG2ProcessLifecycleException("launch failed"); break;
            case "reconnect": _process.WaitError = new AG2ProcessLifecycleException("reconnect timeout"); break;
            case "wrong_identity":
                _process.OnReplacement = () => _adapter.Identity = new AccountIdentityDto("wrong@example.com");
                break;
            case "identity_timeout": _process.OnReplacement = () => _adapter.Identity = null; break;
        }
    }

    private static WinCredEntry Credential(string user, string secret) =>
        new("gemini:antigravity", 1, user, 2, Encoding.UTF8.GetBytes(secret));

    private sealed class SwitchCredentialStore : IWinCredReader, IWinCredWriter
    {
        private WinCredEntry? _current;
        public int WriteCount { get; private set; }
        public Func<int, WinCredEntry, CancellationToken, Task<bool>>? WriteBehavior { get; set; }
        public List<bool> WriteTokenWasCancelled { get; } = [];

        public void Set(WinCredEntry entry)
        {
            Clear();
            _current = Clone(entry);
        }

        public WinCredEntry Snapshot() => Clone(_current!);

        public Task<WinCredEntry?> ReadCredentialAsync(
            string target = "gemini:antigravity",
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<WinCredEntry?>(_current == null ? null : Clone(_current));
        }

        public async Task<bool> WriteCredentialAsync(
            WinCredEntry entry,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            WriteTokenWasCancelled.Add(cancellationToken.IsCancellationRequested);
            if (WriteBehavior != null) return await WriteBehavior(WriteCount, entry, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Set(entry);
            return true;
        }

        public void Clear()
        {
            if (_current?.Blob is { Length: > 0 })
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(_current.Blob);
            _current = null;
        }

        private static WinCredEntry Clone(WinCredEntry entry) =>
            new(entry.Target, entry.Type, entry.UserName, entry.Persistence, (byte[])entry.Blob.Clone());
    }

    private sealed class SwitchAdapter : IAG2Adapter
    {
        public AccountIdentityDto? Identity { get; set; }
        public ActivityStatusDto Activity { get; set; } =
            new("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O"));
        public Ag2StatusDto Status { get; set; } =
            new(true, "HEALTHY", new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O")), "synthetic");
        public Func<CancellationToken, Task<AccountIdentityDto?>>? IdentityBehavior { get; set; }
        public Func<CancellationToken, Task<ActivityStatusDto>>? ActivityBehavior { get; set; }

        public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);
        public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            IdentityBehavior?.Invoke(cancellationToken) ?? Task.FromResult(Identity);
        public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<QuotaSnapshotDto?>(null);
        public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountQuotaObservation(Identity, null));
        public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default) =>
            ActivityBehavior?.Invoke(cancellationToken) ?? Task.FromResult(Activity);
    }

    private sealed class SwitchProcessLifecycle : IAG2ProcessLifecycle
    {
        private readonly AG2ProcessSnapshot _snapshot = new(
            100, DateTime.UtcNow.AddMinutes(-1), @"C:\Synthetic\Antigravity\resources\bin\language_server.exe",
            ["--standalone", "--csrf_token", "synthetic"],
            ["--standalone", "--csrf_token", "[REDACTED]"], 10, DateTime.UtcNow.AddDays(-1),
            new string('A', 64), 1);
        private readonly AG2ProcessGeneration _replacement = new(
            101, DateTime.UtcNow, @"C:\Synthetic\Antigravity\resources\bin\language_server.exe", 2);
        private int _revalidateCalls;

        public Exception? CaptureError { get; set; }
        public Exception? StopError { get; set; }
        public Exception? LaunchError { get; set; }
        public Exception? WaitError { get; set; }
        public Exception? RestoreError { get; set; }
        public int RevalidateErrorOnCall { get; set; }
        public bool GenerationCurrent { get; set; } = true;
        public int StopCount { get; private set; }
        public int QuiesceCount { get; private set; }
        public int LaunchCount { get; private set; }
        public int RestoreCount { get; private set; }
        public bool RollbackTokenWasCancelled { get; private set; }
        public Action? OnReplacement { get; set; }
        public Action? OnRestore { get; set; }
        public Action? OnStop { get; set; }
        public Func<CancellationToken, Task>? StopBehavior { get; set; }
        public Func<CancellationToken, Task>? BeforeKillBehavior { get; set; }
        public Func<CancellationToken, Task>? QuiesceBehavior { get; set; }

        public Task<AG2ProcessSnapshot> CaptureVerifiedAsync(CancellationToken cancellationToken = default) =>
            CaptureError != null ? Task.FromException<AG2ProcessSnapshot>(CaptureError) : Task.FromResult(_snapshot);

        public Task RevalidateAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            _revalidateCalls++;
            if (RevalidateErrorOnCall == _revalidateCalls)
                throw new AG2ProcessLifecycleException("stale synthetic process generation");
            return Task.CompletedTask;
        }

        public async Task StopVerifiedAsync(
            AG2ProcessSnapshot snapshot,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? verifyBeforeKillAsync = null,
            Action? onStopAttempted = null,
            Action? onStopIssued = null)
        {
            if (BeforeKillBehavior != null) await BeforeKillBehavior(cancellationToken);
            if (verifyBeforeKillAsync != null) await verifyBeforeKillAsync(cancellationToken);
            onStopAttempted?.Invoke();
            StopCount++;
            OnStop?.Invoke();
            onStopIssued?.Invoke();
            if (StopBehavior != null) await StopBehavior(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (StopError != null) throw StopError;
        }

        public Task<AG2ProcessGeneration> LaunchAsync(
            AG2ProcessSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            LaunchCount++;
            return LaunchError != null
                ? Task.FromException<AG2ProcessGeneration>(LaunchError)
                : Task.FromResult(_replacement);
        }

        public Task<AG2ProcessGeneration> WaitForHealthyReplacementAsync(
            AG2ProcessSnapshot original,
            AG2ProcessGeneration launched,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (WaitError != null) return Task.FromException<AG2ProcessGeneration>(WaitError);
            OnReplacement?.Invoke();
            return Task.FromResult(_replacement);
        }

        public async Task QuiesceForRollbackAsync(
            AG2ProcessSnapshot original,
            AG2ProcessGeneration? transactionOwnedReplacement,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            QuiesceCount++;
            RollbackTokenWasCancelled = cancellationToken.IsCancellationRequested;
            if (QuiesceBehavior != null) await QuiesceBehavior(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public Task<AG2ProcessGeneration> RestoreAsync(
            AG2ProcessSnapshot original,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            RollbackTokenWasCancelled |= cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            if (RestoreError != null) return Task.FromException<AG2ProcessGeneration>(RestoreError);
            GenerationCurrent = true;
            OnRestore?.Invoke();
            return Task.FromResult(new AG2ProcessGeneration(102, DateTime.UtcNow,
                original.ExecutablePath, 3));
        }

        public Task<bool> IsGenerationCurrentAsync(
            AG2ProcessGeneration generation,
            CancellationToken cancellationToken = default) => Task.FromResult(GenerationCurrent);
    }
}

internal static class SwitchTestListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++) if (values[i] == value) return i;
        return -1;
    }
}
