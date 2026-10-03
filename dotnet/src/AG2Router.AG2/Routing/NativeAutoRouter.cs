using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Security;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using AG2Router.Core.Validation;

namespace AG2Router.AG2.Routing;

public class NativeAutoRouter : INativeAutoRouter
{
    private readonly IAccountStore _accountStore;
    private readonly ISessionVault _sessionVault;
    private readonly IAG2Adapter _adapter;
    private readonly INativeAccountSwitchCoordinator _switchCoordinator;
    private readonly RoutingSafetyGate _safetyGate;
    private readonly string? _configFilePath;
    private readonly IDurableFileWriter? _fileWriter;
    private readonly IQuotaObservationStore? _quotaObservationStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _evaluatingGate = new(1, 1);
    private readonly Dictionary<string, DateTime> _candidateCooldowns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _observedQuotas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ModelQuotaDto>> _observedModelQuotas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CandidateQuotaEvidence> _quotaEvidence = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _activeManualSwitches = new();
    private readonly object _stateLock = new();

    /// <summary>
    /// Interruption admission gate (R02).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Lock Acquisition Hierarchy:</b>
    /// <c>Switch ownership (SwitchGate) -> interruption admission (_interruptionAdmission) -> state lock (_stateLock)</c>.
    /// </para>
    /// <para>
    /// <b>Mutual Exclusion Invariant:</b>
    /// Automatic process interruption (killing the Antigravity instance to apply credentials) and
    /// configuration updates (<see cref="UpdateConfigWithGeneration"/>) share this admission gate.
    /// This guarantees that an auto-switch transaction cannot terminate a process under obsolete
    /// configuration settings, and conversely, a configuration update cannot race or corrupt an
    /// in-flight process termination. Config saves start at admission, before disk persistence,
    /// and never acquire switch ownership from inside it.
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _interruptionAdmission = new(1, 1);
    internal Action? ConfigAdmissionContended { get; set; }
    internal Action? AutomaticAdmissionContended { get; set; }
    internal Func<Task>? BeforeGatePublicationAsync { get; set; }

    private RouterConfigDto _config;
    private long _configGeneration = 1;
    private DateTime? _cooldownUntil;
    private string? _lastActiveAccountId;
    private string? _lastActiveAccountEmail;
    private string? _lastEvaluatedAt;
    private string _lastDecisionReason = "Router initialized";
    private long _manualSwitchEpoch;
    private long _lastSuccessfulManualEpoch;
    private long _nextManualTokenId;
    private bool _manualSwitchPending;
    private bool _disposed;

    private sealed record CandidateQuotaEvidence(DateTimeOffset ObservedAtUtc, string Provenance);

    // Two polling periods tolerate one delayed cycle, while the 30-second cap
    // prevents a slow polling configuration from making old quota actionable.
    internal static TimeSpan CandidateEvidenceLifetime(int pollingIntervalMs) =>
        TimeSpan.FromMilliseconds(Math.Min(2L * pollingIntervalMs, 30_000L));

    internal static bool IsCandidateEvidenceFresh(DateTimeOffset observedAtUtc,
        DateTimeOffset nowUtc, TimeSpan lifetime) =>
        observedAtUtc <= nowUtc && nowUtc - observedAtUtc < lifetime;

    public NativeAutoRouter(
        IAccountStore accountStore,
        ISessionVault sessionVault,
        IAG2Adapter adapter,
        INativeAccountSwitchCoordinator switchCoordinator,
        RouterConfigDto? initialConfig = null,
        RoutingSafetyGate? safetyGate = null,
        string? configFilePath = null,
        IQuotaObservationStore? quotaObservationStore = null,
        TimeProvider? timeProvider = null)
        : this(accountStore, sessionVault, adapter, switchCoordinator, initialConfig, safetyGate, configFilePath, (!string.IsNullOrEmpty(configFilePath) ? new DurableFileWriter() : null), quotaObservationStore, timeProvider)
    {
    }

    internal NativeAutoRouter(
        IAccountStore accountStore,
        ISessionVault sessionVault,
        IAG2Adapter adapter,
        INativeAccountSwitchCoordinator switchCoordinator,
        RouterConfigDto? initialConfig,
        RoutingSafetyGate? safetyGate,
        string? configFilePath,
        IDurableFileWriter? fileWriter,
        IQuotaObservationStore? quotaObservationStore = null,
        TimeProvider? timeProvider = null)
    {
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        _sessionVault = sessionVault ?? throw new ArgumentNullException(nameof(sessionVault));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _switchCoordinator = switchCoordinator ?? throw new ArgumentNullException(nameof(switchCoordinator));
        _configFilePath = configFilePath;
        _fileWriter = fileWriter ?? (!string.IsNullOrEmpty(configFilePath) ? new DurableFileWriter() : null);
        _quotaObservationStore = quotaObservationStore;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (initialConfig != null)
        {
            RouterConfigValidator.Validate(initialConfig);
        }

        RouterConfigDto? loadedConfig = null;
        if (!string.IsNullOrEmpty(_configFilePath) && File.Exists(_configFilePath))
        {
            try
            {
                var json = File.ReadAllText(_configFilePath);
                loadedConfig = JsonSerializer.Deserialize<RouterConfigDto>(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Router configuration file '{_configFilePath}' is malformed.", ex);
            }

            if (loadedConfig != null)
            {
                try
                {
                    RouterConfigValidator.Validate(loadedConfig);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException($"Router configuration file '{_configFilePath}' contains invalid configuration: {ex.Message}", ex);
                }
            }
            else
            {
                throw new InvalidDataException($"Router configuration file '{_configFilePath}' contains invalid configuration.");
            }
        }

        _config = initialConfig ?? loadedConfig ?? new RouterConfigDto();
        _safetyGate = safetyGate ?? new RoutingSafetyGate();
    }

    public RoutingSafetyGate SafetyGate => _safetyGate;

    public RouterConfigDto GetConfig()
    {
        lock (_stateLock)
        {
            return _config;
        }
    }

    public long ConfigGeneration
    {
        get
        {
            lock (_stateLock)
            {
                return _configGeneration;
            }
        }
    }

    public RouterConfigDto UpdateConfig(RouterConfigDto updates) => UpdateConfigWithGeneration(updates).Config;

    /// <summary>
    /// Atomically persists configuration updates and publishes a monotonically increasing generation number (R05).
    /// </summary>
    /// <param name="updates">The validated new router configuration.</param>
    /// <returns>A tuple containing the applied configuration and its unique monotonic generation ID.</returns>
    public (RouterConfigDto Config, long Generation) UpdateConfigWithGeneration(RouterConfigDto updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        RouterConfigValidator.Validate(updates);
        if (!_interruptionAdmission.Wait(0))
        {
            ConfigAdmissionContended?.Invoke();
            _interruptionAdmission.Wait();
        }
        try
        {
            lock (_stateLock)
            {
                if (!string.IsNullOrEmpty(_configFilePath) && _fileWriter != null)
                {
                    var json = JsonSerializer.Serialize(updates, new JsonSerializerOptions { WriteIndented = true });
                    _fileWriter.WriteAtomicAsync(_configFilePath, json, CancellationToken.None).GetAwaiter().GetResult();
                }

                _config = updates;
                _configGeneration++;
                if (!_config.AutoSwitchEnabled &&
                    (_safetyGate.State is RoutingSafetyGateState.SwitchPending or RoutingSafetyGateState.WaitingForIdle or RoutingSafetyGateState.LowQuotaDetected))
                {
                    _safetyGate.Reset("Auto-switch disabled by configuration update.");
                }
                return (_config, _configGeneration);
            }
        }
        finally { _interruptionAdmission.Release(); }
    }

    /// <summary>
    /// Acquires the interruption admission lease for an automatic switch transaction (R02).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for acquiring admission.</param>
    /// <returns>An <see cref="IDisposable"/> lease releasing admission upon disposal.</returns>
    /// <remarks>
    /// Held across the pre-kill verification and process termination (<c>Kill</c>), and released
    /// at <c>onStopIssued</c> before waiting for the process to exit.
    /// </remarks>
    internal async Task<IDisposable> AcquireInterruptionAdmissionAsync(CancellationToken cancellationToken)
    {
        if (!await _interruptionAdmission.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            AutomaticAdmissionContended?.Invoke();
            await _interruptionAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return new InterruptionAdmissionLease(_interruptionAdmission);
    }

    private sealed class InterruptionAdmissionLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    public ManualSwitchToken NotifyManualSwitchStarted(string targetAccountId)
    {
        lock (_stateLock)
        {
            _manualSwitchEpoch++;
            long tokenId = ++_nextManualTokenId;
            _activeManualSwitches[tokenId] = targetAccountId;
            _manualSwitchPending = true;

            if (_safetyGate.State is RoutingSafetyGateState.WaitingForIdle or RoutingSafetyGateState.SwitchPending or RoutingSafetyGateState.LowQuotaDetected)
            {
                _safetyGate.Reset($"Auto-switch cancelled by manual switch to {targetAccountId}.");
                _lastDecisionReason = $"Pending switch cancelled due to manual switch initiation for {targetAccountId}.";
            }
            return new ManualSwitchToken(tokenId);
        }
    }

    public async Task NotifyManualSwitchCompletedAsync(ManualSwitchToken token, NativeSwitchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success)
        {
            lock (_stateLock)
            {
                if (!_activeManualSwitches.Remove(token.Id)) return;
                _manualSwitchEpoch++;
                _manualSwitchPending = _activeManualSwitches.Count > 0;
                ApplyManualSwitchResult(result, _manualSwitchPending);
            }
            return;
        }

        // The coordinator releases this lease before the HTTP callback runs. A second
        // manual transaction may have committed in that gap, so publish only the
        // identity that is still authoritative while owning the same switch resource.
        string resource = _sessionVault.GetVaultPath() + ".switch";
        var switchLock = PathLockRegistry.Get(resource);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        bool lockHeld = false;
        try
        {
            await switchLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            lockHeld = true;
            await using var lease = await CrossProcessFileLease.AcquireAsync(resource, deadline.Token)
                .ConfigureAwait(false);
            string? activeId = await _accountStore.GetActiveAccountIdAsync(deadline.Token).ConfigureAwait(false);
            AccountMetadata? activeAccount = activeId == null ? null :
                await _accountStore.GetAccountAsync(activeId, deadline.Token).ConfigureAwait(false);
            lock (_stateLock)
            {
                if (!_activeManualSwitches.Remove(token.Id)) return;
                _manualSwitchEpoch++;
                _manualSwitchPending = _activeManualSwitches.Count > 0;
                if (!string.Equals(activeId, result.TargetAccountId, StringComparison.Ordinal))
                {
                    _lastActiveAccountId = activeId;
                    _lastActiveAccountEmail = activeAccount?.Email;
                    _lastDecisionReason = "Manual completion superseded by authoritative active identity.";
                    return;
                }
                ApplyManualSwitchResult(result, _manualSwitchPending);
            }
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                if (!_activeManualSwitches.Remove(token.Id)) return;
                _manualSwitchEpoch++;
                _manualSwitchPending = _activeManualSwitches.Count > 0;
                RequireManualRecovery($"Manual completion ownership could not be verified: {AG2Security.RedactSensitiveText(ex.Message)}");
            }
        }
        finally
        {
            if (lockHeld) switchLock.Release();
        }
    }

    private void RequireManualRecovery(string reason)
    {
        if (_safetyGate.State != RoutingSafetyGateState.ManualRecoveryRequired)
        {
            if (_safetyGate.State != RoutingSafetyGateState.Idle)
                _safetyGate.Reset("Switch outcome requires manual recovery.");
            _safetyGate.Transition(RoutingSafetyGateState.ManualRecoveryRequired, reason);
        }
        _config = _config with { AutoSwitchEnabled = false };
        _lastDecisionReason = reason;
    }

    private void ApplyManualSwitchResult(NativeSwitchResult result, bool hasRemaining)
    {
        if (result.Success)
        {
            _lastSuccessfulManualEpoch = _manualSwitchEpoch;
            _lastActiveAccountId = result.TargetAccountId;
            _lastActiveAccountEmail = result.TargetEmail;
            _observedQuotas.Remove(result.TargetAccountId);
            _observedModelQuotas.Remove(result.TargetAccountId);
            _quotaEvidence.Remove(result.TargetAccountId);
            if (_safetyGate.State != RoutingSafetyGateState.ManualRecoveryRequired)
            {
                if (_safetyGate.State != RoutingSafetyGateState.Cooldown)
                {
                    if (_safetyGate.State != RoutingSafetyGateState.Idle)
                    {
                        _safetyGate.Reset("Manual switch completed; resetting before cooldown.");
                    }
                    _safetyGate.Transition(RoutingSafetyGateState.Cooldown, "Stabilizing after manual switch");
                }
                _cooldownUntil = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(30);
                _lastDecisionReason = $"Manual switch succeeded to {result.TargetEmail}. In cooldown.";
            }
        }
        else if (result.ManualRecoveryRequired)
        {
            if (_safetyGate.State != RoutingSafetyGateState.ManualRecoveryRequired)
            {
                if (_safetyGate.State != RoutingSafetyGateState.Idle)
                {
                    _safetyGate.Reset("Manual switch failed; resetting before manual recovery.");
                }
                _safetyGate.Transition(RoutingSafetyGateState.ManualRecoveryRequired, $"Manual recovery required: {result.Message}");
            }
            _config = _config with { AutoSwitchEnabled = false };
            _lastDecisionReason = $"Manual switch failed; manual recovery required: {result.Message}";
        }
        else if (!hasRemaining &&
                 _safetyGate.State == RoutingSafetyGateState.SwitchInProgress &&
                 _switchCoordinator.GetStatus().ActiveTransactionId == null)
        {
            _safetyGate.Reset("Manual request ended after contending with automatic switch.");
            _safetyGate.Transition(RoutingSafetyGateState.Cooldown, "Stabilizing after switch contention");
            _cooldownUntil = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(30);
        }
    }

    public RouterStatusDto GetStatus()
    {
        lock (_stateLock)
        {
            return new RouterStatusDto(
                State: _safetyGate.State,
                AutoSwitchEnabled: _config.AutoSwitchEnabled,
                ActiveAccountId: _lastActiveAccountId,
                ActiveAccountEmail: _lastActiveAccountEmail,
                PendingTargetAccountId: _safetyGate.TargetAccountId,
                LastEvaluatedAt: _lastEvaluatedAt,
                LastDecisionReason: _lastDecisionReason,
                Config: _config
            );
        }
    }

    public async Task<CandidateEvidenceStatusDto> GetCandidateEvidenceStatusAsync(CancellationToken cancellationToken = default)
    {
        var config = GetConfig();
        var model = CandidateSelector.CanonicalizeModelKey(config.WorkloadModelKey);
        if (_quotaObservationStore == null)
            return new(model, config.MinimumCandidateQuotaPercent, false, []);

        // Do not populate the routing cache or probe inactive accounts for a dashboard read.
        var observations = QuotaObservationEvidence.Consolidate(
            await _quotaObservationStore.GetAllObservationsAsync(cancellationToken).ConfigureAwait(false));
        var accounts = await _accountStore.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var activeId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var candidates = accounts.Where(a => a.Id != activeId).Select(account =>
        {
            var observation = observations.SingleOrDefault(o => o.AccountId == account.Id && o.ModelKey == model);
            string state = model == null ? "UNCONFIGURED" : observation == null ? "NOT_OBSERVED" :
                observation.ObservedAtUtc > now ? "INVALID" :
                now - observation.ObservedAtUtc >= CandidateSelector.InactiveCandidateEvidenceLifetime ? "STALE" :
                observation.RemainingFraction == null ? "UNKNOWN" :
                observation.RemainingFraction <= 0 ? "EXHAUSTED" :
                QuotaObservationEvidence.IsUsable(observation, now, config.MinimumCandidateQuotaPercent) ? "USABLE" : "BELOW_MINIMUM";
            // Old percentages are not current capacity. Reset timestamps never change this state.
            double? remaining = state is "USABLE" or "BELOW_MINIMUM" or "EXHAUSTED" ? observation?.RemainingFraction : null;
            return new CandidateQuotaStatusDto(account.Id, state, remaining, observation?.ObservedAtUtc,
                observation == null ? null : (now - observation.ObservedAtUtc).TotalSeconds);
        }).OrderBy(c => c.AccountId, StringComparer.Ordinal).ToArray();
        return new(model, config.MinimumCandidateQuotaPercent, true, candidates);
    }

    public void ResetManualRecovery()
    {
        lock (_stateLock)
        {
            if (_safetyGate.State == RoutingSafetyGateState.ManualRecoveryRequired)
            {
                _safetyGate.Reset("Manual recovery state cleared by operator.");
                _lastDecisionReason = "Manual recovery cleared. Ready to resume monitoring.";
            }
        }
    }

    // Synthetic injection for tests. Production evidence only comes from the
    // coherent active-account GetUserStatus observation in EvaluateCycleAsync.
    internal void SetObservedQuota(string accountId, double remainingFraction,
        IReadOnlyList<ModelQuotaDto>? modelQuotas = null, DateTimeOffset? observedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        lock (_stateLock)
        {
            _observedQuotas[accountId] = remainingFraction;
            _quotaEvidence[accountId] = new CandidateQuotaEvidence(
                observedAtUtc ?? _timeProvider.GetUtcNow(), "SyntheticTestObservation");
            if (modelQuotas != null)
            {
                _observedModelQuotas[accountId] = modelQuotas;
            }
            else
            {
                _observedModelQuotas.Remove(accountId);
            }
        }
    }

    public async Task<SelectionResult> EvaluateCycleAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return new SelectionResult(false, "Evaluation cancelled or router disposed.", _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
        }

        // Concurrency fence: ensure zero overlapping evaluation cycles
        if (!await _evaluatingGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new SelectionResult(false, "Evaluation already in progress.", _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
        }

        try
        {
            long planEpoch;
            lock (_stateLock)
            {
                planEpoch = _manualSwitchEpoch;
                if (_manualSwitchPending || _activeManualSwitches.Count > 0)
                    return new SelectionResult(false, "Manual switch in progress.", _lastActiveAccountId,
                        null, null, Array.Empty<CandidateEvaluation>());
            }
            string nowIso = _timeProvider.GetUtcNow().UtcDateTime.ToString("o");

            if (!_switchCoordinator.CanAdmitSwitch(out var admissionBlockReason))
            {
                var status = _switchCoordinator.GetStatus();
                bool isQuarantinedOrRecovery = status.QuarantineActive ||
                    !string.Equals(status.JournalRecoveryState, JournalRecoveryStates.None, StringComparison.Ordinal) ||
                    RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath() + ".switch").IsMarked ||
                    RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath()).IsMarked;

                string reason = admissionBlockReason ?? "Account lifecycle recovery is unresolved; automatic switching is halted.";
                lock (_stateLock)
                {
                    _lastEvaluatedAt = nowIso;
                    if (isQuarantinedOrRecovery)
                    {
                        RequireManualRecovery(reason);
                    }
                    else
                    {
                        _lastDecisionReason = reason;
                    }
                }
                return new SelectionResult(false, reason, _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
            }

            // Check circuit breaker
            if (_safetyGate.State == RoutingSafetyGateState.ManualRecoveryRequired)
            {
                lock (_stateLock)
                {
                    _lastEvaluatedAt = nowIso;
                    _lastDecisionReason = "Automatic switching halted: manual recovery required.";
                }
                return new SelectionResult(false, "Automatic switching halted: manual recovery required.", _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
            }

            // Check stabilization cooldown. A health probe is only evidence for the
            // cooldown that launched it, never for a newer manual outcome.
            DateTime? probedCooldownUntil;
            lock (_stateLock)
            {
                probedCooldownUntil = _cooldownUntil;
                if (_safetyGate.State == RoutingSafetyGateState.Cooldown &&
                    _cooldownUntil.HasValue && _timeProvider.GetUtcNow().UtcDateTime < _cooldownUntil.Value)
                {
                    _lastEvaluatedAt = nowIso;
                    _lastDecisionReason = $"In cooldown until {_cooldownUntil.Value:O}.";
                    return new SelectionResult(false, $"In cooldown until {_cooldownUntil.Value:O}.", _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
                }
            }

            if (_safetyGate.State == RoutingSafetyGateState.Cooldown)
            {
                string? probedActiveId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
                // Cooldown timer elapsed: check telemetry health before returning to IDLE
                var probeStatus = await _adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                string cooldownResource = _sessionVault.GetVaultPath() + ".switch";
                if (RecoveryQuarantineRegistry.Get(cooldownResource).IsMarked)
                {
                    lock (_stateLock) RequireManualRecovery("Account lifecycle is unresolved; manual recovery is required.");
                    return new SelectionResult(false, "Account lifecycle is unresolved; manual recovery is required.",
                        _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
                }
                var cooldownLock = PathLockRegistry.Get(cooldownResource);
                using var cooldownDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cooldownDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                try { await cooldownLock.WaitAsync(cooldownDeadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lock (_stateLock) RequireManualRecovery("Account lifecycle ownership did not become available.");
                    return new SelectionResult(false, "Account lifecycle ownership did not become available.",
                        _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
                }
                try
                {
                    if (RecoveryQuarantineRegistry.Get(cooldownResource).IsMarked)
                    {
                        lock (_stateLock) RequireManualRecovery("Account lifecycle is unresolved; manual recovery is required.");
                        return new SelectionResult(false, "Account lifecycle is unresolved; manual recovery is required.",
                            _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
                    }
                    await using var lease = await CrossProcessFileLease.AcquireAsync(cooldownResource, cancellationToken)
                        .ConfigureAwait(false);
                    if (RecoveryQuarantineRegistry.Get(cooldownResource).IsMarked ||
                        RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath()).IsMarked)
                    {
                        lock (_stateLock) RequireManualRecovery("Account lifecycle is unresolved; manual recovery is required.");
                        return new SelectionResult(false, "Account lifecycle is unresolved; manual recovery is required.",
                            _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
                    }
                    string? activeAfterProbe = await _accountStore.GetActiveAccountIdAsync(cancellationToken)
                        .ConfigureAwait(false);
                    lock (_stateLock)
                    {
                        if (!IsEvaluationCurrentLocked(planEpoch) ||
                            _safetyGate.State != RoutingSafetyGateState.Cooldown ||
                            _cooldownUntil != probedCooldownUntil ||
                            !string.Equals(activeAfterProbe, probedActiveId, StringComparison.Ordinal))
                            return new SelectionResult(false, "Cooldown probe was superseded.", _lastActiveAccountId,
                                null, null, Array.Empty<CandidateEvaluation>());

                        if (probeStatus.Connected && probeStatus.Status is not ("DEGRADED" or "OFFLINE" or "ERROR"))
                        {
                            _safetyGate.Transition(RoutingSafetyGateState.Idle, "Cooldown elapsed; returning to monitoring.");
                            _cooldownUntil = null;
                        }
                        else
                        {
                            _lastEvaluatedAt = nowIso;
                            _lastDecisionReason = "Cooldown elapsed but telemetry remains unavailable.";
                            return new SelectionResult(false, "Cooldown elapsed but telemetry remains unavailable.",
                                _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
                        }
                    }
                }
                finally
                {
                    cooldownLock.Release();
                }
            }

            // Check Antigravity availability
            var ag2Status = await _adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!ag2Status.Connected || ag2Status.Status is "DEGRADED" or "OFFLINE" or "ERROR")
            {
                lock (_stateLock)
                {
                    if (!IsEvaluationCurrentLocked(planEpoch))
                        return new SelectionResult(false, "Status observation was superseded.",
                            _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
                    if (_safetyGate.State == RoutingSafetyGateState.WaitingForIdle)
                        _safetyGate.Reset("Antigravity offline or disconnected; pending switch cancelled.");
                    _lastEvaluatedAt = nowIso;
                    _lastDecisionReason = ag2Status.Message ?? "Antigravity offline or not detected.";
                }
                return new SelectionResult(false, ag2Status.Message ?? "Antigravity offline or not detected.", _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
            }

            // Clean expired candidate cooldowns
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var expiredKeys = _candidateCooldowns.Where(kvp => kvp.Value <= nowUtc).Select(kvp => kvp.Key).ToList();
            foreach (var key in expiredKeys)
            {
                _candidateCooldowns.Remove(key);
            }
            var activeCooldownIds = _candidateCooldowns.Keys.ToHashSet(StringComparer.Ordinal);

            // Fetch accounts and telemetry
            var activeAccountId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
            var accounts = await _accountStore.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
            var activeAccount = activeAccountId != null
                ? await _accountStore.GetAccountAsync(activeAccountId, cancellationToken).ConfigureAwait(false)
                : null;

            var liveBeforeQuota = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            if (!IdentityMatches(activeAccount, liveBeforeQuota))
                return RejectUnprovenActiveIdentity(nowIso, planEpoch);
            var observation = await _adapter.GetAccountQuotaObservationAsync(cancellationToken).ConfigureAwait(false);
            var quotaObservedAtUtc = _timeProvider.GetUtcNow();
            if (!IdentityMatches(activeAccount, observation.Account))
                return RejectUnprovenActiveIdentity(nowIso, planEpoch);
            var quotaSnapshot = observation.Quota;

            var requestedModel = await _adapter.GetRequestedModelAsync(cancellationToken).ConfigureAwait(false);
            if (!IdentityMatches(activeAccount, requestedModel.Account) ||
                string.IsNullOrWhiteSpace(requestedModel.ModelOrTier))
                return RejectRequestedModel(nowIso, planEpoch, activeAccountId,
                    "Requested workload model is unknown; automatic switching requires live model evidence.");
            string requestedModelKey = requestedModel.ModelOrTier.Trim().ToLowerInvariant();

            // Only the proven requested model can create routing pressure. A low
            // quota on another model is not evidence about the current workload.
            double? currentQuotaFraction = null;
            if (quotaSnapshot?.Models != null && quotaSnapshot.Models.Count > 0)
            {
                var matchingModels = quotaSnapshot.Models
                    .Where(m => string.Equals(CandidateSelector.GetModelKey(m), requestedModelKey,
                        StringComparison.Ordinal))
                    .ToList();
                if (matchingModels.Count > 0 && matchingModels.Any(m => m.IsExhausted ||
                    (m.RemainingFraction.HasValue && m.RemainingFraction.Value <= 0.0)))
                {
                    currentQuotaFraction = 0.0;
                }
                else if (matchingModels.Count > 0 && matchingModels.All(m =>
                    m.RemainingFraction.HasValue && double.IsFinite(m.RemainingFraction.Value)))
                {
                    currentQuotaFraction = matchingModels.Min(m => m.RemainingFraction!.Value);
                }
            }

            Dictionary<string, double> observedSnapshot;
            Dictionary<string, IReadOnlyList<ModelQuotaDto>> observedModelSnapshot;
            RouterConfigDto configSnapshot;
            string switchResource = _sessionVault.GetVaultPath() + ".switch";
            if (RecoveryQuarantineRegistry.Get(switchResource).IsMarked)
            {
                lock (_stateLock) RequireManualRecovery("Account lifecycle is unresolved; manual recovery is required.");
                return new SelectionResult(false, "Account lifecycle is unresolved; manual recovery is required.",
                    activeAccountId, null, null, Array.Empty<CandidateEvaluation>());
            }
            var switchLock = PathLockRegistry.Get(switchResource);
            using var ownershipDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ownershipDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            try { await switchLock.WaitAsync(ownershipDeadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lock (_stateLock) RequireManualRecovery("Account lifecycle ownership did not become available.");
                return new SelectionResult(false, "Account lifecycle ownership did not become available.",
                    activeAccountId, null, null, Array.Empty<CandidateEvaluation>());
            }
            try
            {
                if (RecoveryQuarantineRegistry.Get(switchResource).IsMarked)
                {
                    lock (_stateLock) RequireManualRecovery("Account lifecycle is unresolved; manual recovery is required.");
                    return new SelectionResult(false, "Account lifecycle is unresolved; manual recovery is required.",
                        activeAccountId, null, null, Array.Empty<CandidateEvaluation>());
                }
                await using var lease = await CrossProcessFileLease.AcquireAsync(switchResource, cancellationToken)
                    .ConfigureAwait(false);
                if (RecoveryQuarantineRegistry.Get(switchResource).IsMarked ||
                    RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath()).IsMarked)
                {
                    lock (_stateLock) RequireManualRecovery("Account lifecycle is unresolved; manual recovery is required.");
                    return new SelectionResult(false, "Account lifecycle is unresolved; manual recovery is required.",
                        activeAccountId, null, null, Array.Empty<CandidateEvaluation>());
                }
                var activeAfterQuota = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
                var activeMetadataAfterQuota = activeAfterQuota == null ? null :
                    await _accountStore.GetAccountAsync(activeAfterQuota, cancellationToken).ConfigureAwait(false);
                var liveAfterQuota = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
                List<AccountModelQuotaObservation>? observationsToRecord = null;
                lock (_stateLock)
                {
                    if (_manualSwitchEpoch != planEpoch || _manualSwitchPending ||
                        !string.Equals(activeAfterQuota, activeAccountId, StringComparison.Ordinal))
                        return new SelectionResult(false, "Active account changed during quota observation.",
                            activeAfterQuota, null, null, Array.Empty<CandidateEvaluation>());
                    if (!IdentityMatches(activeMetadataAfterQuota, liveAfterQuota) ||
                        !string.Equals(activeAccount?.Email, activeMetadataAfterQuota?.Email, StringComparison.OrdinalIgnoreCase))
                        return RejectUnprovenActiveIdentityLocked(nowIso);
                    _lastActiveAccountId = activeAccountId;
                    _lastActiveAccountEmail = activeAccount?.Email;
                    if (activeAccountId != null)
                    {
                        if (currentQuotaFraction.HasValue && double.IsFinite(currentQuotaFraction.Value))
                        {
                            _observedQuotas[activeAccountId] = currentQuotaFraction.Value;
                            _quotaEvidence[activeAccountId] = new CandidateQuotaEvidence(
                                quotaObservedAtUtc, "ActiveGetUserStatus");
                        }
                        else
                        {
                            _observedQuotas.Remove(activeAccountId);
                            _quotaEvidence.Remove(activeAccountId);
                        }

                        if (quotaSnapshot?.Models != null)
                        {
                            _observedModelQuotas[activeAccountId] = quotaSnapshot.Models;
                            if (_quotaObservationStore != null)
                            {
                                observationsToRecord = QuotaObservationEvidence.Capture(activeAccountId,
                                    quotaSnapshot.Models, quotaObservedAtUtc, "ActiveGetUserStatus").ToList();
                            }
                        }
                        else
                        {
                            _observedModelQuotas.Remove(activeAccountId);
                            if (_quotaObservationStore != null)
                                observationsToRecord = [];
                        }
                    }
                    configSnapshot = _config;
                    var nowForCandidates = _timeProvider.GetUtcNow();
                    var lifetime = CandidateEvidenceLifetime(configSnapshot.PollingIntervalMs);
                    var freshIds = _quotaEvidence
                        .Where(kvp => IsCandidateEvidenceFresh(kvp.Value.ObservedAtUtc,
                            nowForCandidates, lifetime))
                        .Select(kvp => kvp.Key)
                        .ToHashSet(StringComparer.Ordinal);
                    observedSnapshot = _observedQuotas
                        .Where(kvp => freshIds.Contains(kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
                    observedModelSnapshot = _observedModelQuotas
                        .Where(kvp => freshIds.Contains(kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
                }

                if (_quotaObservationStore != null && activeAccountId != null && observationsToRecord != null)
                {
                    try
                    {
                        await _quotaObservationStore.RecordCompleteSnapshotAsync(
                            activeAccountId, observationsToRecord, quotaObservedAtUtc, "ActiveGetUserStatus", cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        lock (_stateLock)
                        {
                            _lastEvaluatedAt = nowIso;
                            _lastDecisionReason = $"Quota observation store failed closed: {ex.Message}";
                        }
                        return new SelectionResult(false, $"Quota observation store failed closed: {ex.Message}",
                            activeAccountId, currentQuotaFraction, null, Array.Empty<CandidateEvaluation>());
                    }
                }
            }
            finally
            {
                switchLock.Release();
            }

            var accountQuotas = new Dictionary<string, double>(observedSnapshot, StringComparer.Ordinal);

            var vaultedAccountIds = (await _sessionVault.ListStoredAccountIdsAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

            lock (_stateLock)
            {
                if (_manualSwitchEpoch != planEpoch || _manualSwitchPending || _activeManualSwitches.Count > 0)
                    return new SelectionResult(false, "Manual switch superseded evaluation.", _lastActiveAccountId,
                        null, null, Array.Empty<CandidateEvaluation>());
            }

            IReadOnlyDictionary<string, AccountModelQuotaObservation>? candidateModelObservations = null;
            if (_quotaObservationStore != null)
            {
                try
                {
                    var durableObservations = QuotaObservationEvidence.Consolidate(await _quotaObservationStore.GetAllObservationsAsync(cancellationToken).ConfigureAwait(false));
                    candidateModelObservations = durableObservations
                        .Where(o => string.Equals(o.ModelKey, requestedModelKey, StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(o => o.AccountId, StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    lock (_stateLock)
                    {
                        _lastEvaluatedAt = nowIso;
                        _lastDecisionReason = $"Quota observation store failed closed: {ex.Message}";
                    }
                    return new SelectionResult(false, $"Quota observation store failed closed: {ex.Message}",
                        activeAccountId, currentQuotaFraction, null, Array.Empty<CandidateEvaluation>());
                }
            }

            var selection = CandidateSelector.SelectBestCandidate(
                activeAccountId,
                currentQuotaFraction,
                accounts,
                accountQuotas,
                configSnapshot,
                vaultedAccountIds,
                activeCooldownIds,
                observedModelSnapshot,
                [requestedModelKey],
                candidateModelObservations,
                _timeProvider.GetUtcNow()
            );

            if (BeforeGatePublicationAsync is { } beforeGatePublication)
                await beforeGatePublication().ConfigureAwait(false);

            // Publish the plan and gate progression under the same owner as manual
            // start/completion. A stale selection cannot create a pending switch.
            bool autoSwitchEnabled;
            lock (_stateLock)
            {
                if (!IsEvaluationCurrentLocked(planEpoch))
                    return new SelectionResult(false, "Manual switch superseded evaluation.", _lastActiveAccountId,
                        null, null, Array.Empty<CandidateEvaluation>());
                autoSwitchEnabled = _config.AutoSwitchEnabled;
                if (selection.ShouldSwitch && selection.BestCandidate != null)
                {
                    if (!_switchCoordinator.CanAdmitSwitch(out var switchAdmissionReason))
                    {
                        var status = _switchCoordinator.GetStatus();
                        bool isQuarantinedOrRecovery = status.QuarantineActive ||
                            !string.Equals(status.JournalRecoveryState, JournalRecoveryStates.None, StringComparison.Ordinal) ||
                            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath() + ".switch").IsMarked ||
                            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath()).IsMarked;

                        string reason = switchAdmissionReason ?? "Account lifecycle recovery is unresolved; automatic switching is halted.";
                        selection = new SelectionResult(false, reason, selection.CurrentAccountId, null, null, selection.Candidates);
                        _lastEvaluatedAt = nowIso;
                        _lastDecisionReason = reason;

                        if (isQuarantinedOrRecovery)
                        {
                            RequireManualRecovery(reason);
                        }
                        else if (_safetyGate.State is RoutingSafetyGateState.WaitingForIdle or
                                 RoutingSafetyGateState.SwitchPending or RoutingSafetyGateState.LowQuotaDetected)
                        {
                            _safetyGate.Reset(reason);
                        }

                        return selection;
                    }
                }
                _lastEvaluatedAt = nowIso;
                _lastDecisionReason = selection.Reason;
                if (selection.ShouldSwitch && selection.BestCandidate != null)
                {
                    if (_safetyGate.State == RoutingSafetyGateState.Idle)
                        _safetyGate.Transition(RoutingSafetyGateState.LowQuotaDetected,
                            selection.Reason, selection.BestCandidate.Account.Id);
                    if (autoSwitchEnabled && _safetyGate.State == RoutingSafetyGateState.LowQuotaDetected)
                        _safetyGate.Transition(RoutingSafetyGateState.SwitchPending,
                            $"Auto-switch queued for candidate {selection.BestCandidate.Account.Email}",
                            selection.BestCandidate.Account.Id);
                }
                else if (_safetyGate.State is RoutingSafetyGateState.WaitingForIdle or
                         RoutingSafetyGateState.SwitchPending or RoutingSafetyGateState.LowQuotaDetected)
                    _safetyGate.Reset("Quota conditions normalized or no candidates eligible; returning to IDLE.");
            }

            if (selection.ShouldSwitch && selection.BestCandidate != null && autoSwitchEnabled)
            {
                var activity = await _adapter.GetActivityStateAsync(cancellationToken).ConfigureAwait(false);
                var latestRequestedModel = await _adapter.GetRequestedModelAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!IdentityMatches(activeAccount, latestRequestedModel.Account) ||
                    !string.Equals(latestRequestedModel.ModelOrTier?.Trim(), requestedModelKey,
                        StringComparison.OrdinalIgnoreCase))
                    return RejectRequestedModel(nowIso, planEpoch, activeAccountId,
                        "Requested workload model changed or became unknown; pending switch was cancelled.");
                var assessment = _safetyGate.AssessActivity(activity);
                bool execute = false;
                lock (_stateLock)
                {
                    if (!IsAutomaticPlanCurrentLocked(planEpoch))
                        return new SelectionResult(false, "Manual switch superseded activity assessment.",
                            _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());

                    if (!assessment.CanProceed)
                    {
                        if (_safetyGate.State == RoutingSafetyGateState.SwitchPending)
                            _safetyGate.Transition(RoutingSafetyGateState.WaitingForIdle,
                                assessment.Reason, selection.BestCandidate.Account.Id);
                        else if (_safetyGate.State == RoutingSafetyGateState.WaitingForIdle &&
                                 !string.Equals(_safetyGate.TargetAccountId,
                                     selection.BestCandidate.Account.Id, StringComparison.Ordinal))
                            _safetyGate.Transition(RoutingSafetyGateState.WaitingForIdle,
                                $"Target updated to {selection.BestCandidate.Account.Email}",
                                selection.BestCandidate.Account.Id);
                    }
                    else if (_safetyGate.State is RoutingSafetyGateState.SwitchPending or RoutingSafetyGateState.WaitingForIdle)
                    {
                        _safetyGate.Transition(RoutingSafetyGateState.SwitchInProgress,
                            $"Antigravity 2 is confirmed IDLE. Executing automatic switch to {selection.BestCandidate.Account.Email}.",
                            selection.BestCandidate.Account.Id);
                        execute = true;
                    }
                }
                if (execute)
                {
                    await ExecuteSwitchAsync(selection.BestCandidate.Account.Id, activeAccountId, planEpoch,
                        requestedModelKey, configSnapshot,
                        candidateModelObservations?.GetValueOrDefault(selection.BestCandidate.Account.Id),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return selection;
        }
        finally
        {
            _evaluatingGate.Release();
        }
    }

    private bool IsAutomaticPlanCurrent(long epoch, string candidateAccountId,
        string? requestedModelKey, RouterConfigDto planConfig, AccountModelQuotaObservation? selectedEvidence)
    {
        lock (_stateLock)
        {
            // Bind all decision inputs, including source pressure, to the immutable
            // selection snapshot. A changed configuration requires a fresh cycle.
            if (!IsAutomaticPlanCurrentLocked(epoch) || _config != planConfig) return false;
            if (_quotaObservationStore == null)
                return _quotaEvidence.TryGetValue(candidateAccountId, out var memoryEvidence) &&
                    IsCandidateEvidenceFresh(memoryEvidence.ObservedAtUtc, _timeProvider.GetUtcNow(),
                        CandidateEvidenceLifetime(_config.PollingIntervalMs));
            if (selectedEvidence == null || selectedEvidence.AccountId != candidateAccountId ||
                selectedEvidence.ModelKey != requestedModelKey ||
                !string.Equals(CandidateSelector.CanonicalizeModelKey(_config.WorkloadModelKey), requestedModelKey, StringComparison.Ordinal))
                return false;
        }
        try
        {
            // The coordinator's synchronous admission callback runs outside the router
            // state lock. Read the authoritative file again at every admission boundary.
            var current = _quotaObservationStore.GetObservationAsync(candidateAccountId, requestedModelKey!)
                .GetAwaiter().GetResult();
            if (current != selectedEvidence || !QuotaObservationEvidence.IsUsable(selectedEvidence,
                _timeProvider.GetUtcNow(), planConfig.MinimumCandidateQuotaPercent)) return false;
            lock (_stateLock) return IsAutomaticPlanCurrentLocked(epoch) &&
                _config == planConfig;
        }
        catch { return false; }
    }

    private bool IsBackendRecoveryBlocked()
    {
        return !_switchCoordinator.CanAdmitSwitch(out _) ||
            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath() + ".switch").IsMarked ||
            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath()).IsMarked;
    }

    private bool IsEvaluationCurrentLocked(long epoch) =>
        !_manualSwitchPending && _activeManualSwitches.Count == 0 &&
        _manualSwitchEpoch == epoch &&
        _safetyGate.State != RoutingSafetyGateState.ManualRecoveryRequired;

    private bool IsAutomaticPlanCurrentLocked(long epoch) =>
        IsEvaluationCurrentLocked(epoch) && _config.AutoSwitchEnabled;

    private static bool IdentityMatches(AccountMetadata? account, AccountIdentityDto? live) =>
        account != null && !string.IsNullOrWhiteSpace(account.Email) &&
        !string.IsNullOrWhiteSpace(live?.Email) &&
        string.Equals(account.Email.Trim(), live.Email.Trim(), StringComparison.OrdinalIgnoreCase);

    private SelectionResult RejectRequestedModel(
        string nowIso, long planEpoch, string? activeAccountId, string reason)
    {
        lock (_stateLock)
        {
            if (!IsEvaluationCurrentLocked(planEpoch))
                return new SelectionResult(false, "Manual switch superseded model observation.",
                    activeAccountId, null, null, Array.Empty<CandidateEvaluation>());
            _lastEvaluatedAt = nowIso;
            _lastDecisionReason = reason;
            if (_safetyGate.State is RoutingSafetyGateState.SwitchPending or
                RoutingSafetyGateState.WaitingForIdle or RoutingSafetyGateState.LowQuotaDetected)
                _safetyGate.Reset(reason);
        }
        return new SelectionResult(false, reason, activeAccountId, null, null,
            Array.Empty<CandidateEvaluation>());
    }

    private SelectionResult RejectUnprovenActiveIdentity(string nowIso, long planEpoch)
    {
        lock (_stateLock)
        {
            if (!IsEvaluationCurrentLocked(planEpoch))
                return new SelectionResult(false, "Manual switch superseded identity observation.",
                    _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
            return RejectUnprovenActiveIdentityLocked(nowIso);
        }
    }

    private SelectionResult RejectUnprovenActiveIdentityLocked(string nowIso)
    {
        const string reason = "Live identity does not match the active account metadata; routing is paused.";
        if (_safetyGate.State != RoutingSafetyGateState.ManualRecoveryRequired)
        {
            _lastActiveAccountId = null;
            _lastActiveAccountEmail = null;
            _lastEvaluatedAt = nowIso;
            _lastDecisionReason = reason;
            if (_safetyGate.State is RoutingSafetyGateState.SwitchPending or
                RoutingSafetyGateState.WaitingForIdle or RoutingSafetyGateState.LowQuotaDetected)
                _safetyGate.Reset(reason);
        }
        return new SelectionResult(false, reason, null, null, null,
            Array.Empty<CandidateEvaluation>());
    }

    private async Task ExecuteSwitchAsync(
        string targetAccountId, string? expectedActiveAccountId, long planEpoch,
        string? requestedModelKey, RouterConfigDto planConfig,
        AccountModelQuotaObservation? selectedEvidence, CancellationToken cancellationToken)
    {
        if (!IsAutomaticPlanCurrent(planEpoch, targetAccountId, requestedModelKey, planConfig, selectedEvidence))
        {
            lock (_stateLock)
            {
                if (!_manualSwitchPending && _activeManualSwitches.Count == 0 &&
                    _safetyGate.State == RoutingSafetyGateState.SwitchInProgress &&
                    _switchCoordinator.GetStatus().ActiveTransactionId == null)
                    _safetyGate.Reset("Stale automatic plan rejected before switch admission.");
            }
            return;
        }
        if (!_switchCoordinator.CanAdmitSwitch(out var executeAdmissionReason))
        {
            var status = _switchCoordinator.GetStatus();
            bool isQuarantinedOrRecovery = status.QuarantineActive ||
                !string.Equals(status.JournalRecoveryState, JournalRecoveryStates.None, StringComparison.Ordinal) ||
                RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath() + ".switch").IsMarked ||
                RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath()).IsMarked;

            string reason = executeAdmissionReason ?? "Account lifecycle recovery is unresolved; automatic switching is halted.";
            lock (_stateLock)
            {
                if (isQuarantinedOrRecovery)
                {
                    RequireManualRecovery(reason);
                }
                else if (_safetyGate.State == RoutingSafetyGateState.SwitchInProgress &&
                         _switchCoordinator.GetStatus().ActiveTransactionId == null)
                {
                    _safetyGate.Reset(reason);
                }
            }
            return;
        }

        NativeSwitchResult switchResult;
        try
        {
            switchResult = await _switchCoordinator.SwitchAutomaticallyAsync(
                targetAccountId, expectedActiveAccountId,
                () => IsAutomaticPlanCurrent(planEpoch, targetAccountId, requestedModelKey, planConfig, selectedEvidence),
                requestedModelKey, planConfig.MinimumCandidateQuotaPercent,
                AcquireInterruptionAdmissionAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                RequireManualRecovery(
                    $"Automatic switch outcome is uncertain: {AG2Security.RedactSensitiveText(ex.Message)}");
            }
            return;
        }

        // Reconcile a successful transaction while owning the same resource as manual
        // switching. A manual coordinator may have committed a newer identity before
        // its completion callback has published the epoch to this router.
        SemaphoreSlim? publicationLock = null;
        CrossProcessFileLease? publicationLease = null;
        bool publicationLockHeld = false;
        string? authoritativeActiveId = null;
        try
        {
            if (switchResult.Success)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                string resource = _sessionVault.GetVaultPath() + ".switch";
                publicationLock = PathLockRegistry.Get(resource);
                await publicationLock.WaitAsync(deadline.Token).ConfigureAwait(false);
                publicationLockHeld = true;
                publicationLease = await CrossProcessFileLease.AcquireAsync(resource, deadline.Token).ConfigureAwait(false);
                authoritativeActiveId = await _accountStore.GetActiveAccountIdAsync(deadline.Token).ConfigureAwait(false);
            }

            // Manual completion and automatic terminal publication use the same state owner.
            lock (_stateLock)
            {
            if (switchResult.ManualRecoveryRequired)
            {
                RequireManualRecovery($"Automatic switching halted: manual recovery required ({switchResult.Message}).");
                return;
            }
            if (_lastSuccessfulManualEpoch > planEpoch) return;
            if (_safetyGate.State == RoutingSafetyGateState.ManualRecoveryRequired) return;

            if (switchResult.Success)
            {
                if (!string.Equals(authoritativeActiveId, switchResult.TargetAccountId, StringComparison.Ordinal))
                {
                    if (!_manualSwitchPending && _safetyGate.State == RoutingSafetyGateState.SwitchInProgress)
                        _safetyGate.Reset("Automatic result superseded by a newer active identity.");
                    return;
                }
                _lastActiveAccountId = switchResult.TargetAccountId;
                _lastActiveAccountEmail = switchResult.TargetEmail;
                _observedQuotas.Remove(switchResult.TargetAccountId);
                _observedModelQuotas.Remove(switchResult.TargetAccountId);
                _quotaEvidence.Remove(switchResult.TargetAccountId);
                if (_safetyGate.State == RoutingSafetyGateState.SwitchInProgress)
                {
                    _safetyGate.Transition(RoutingSafetyGateState.Verifying,
                        $"Verifying target identity {switchResult.TargetEmail}");
                    _safetyGate.Transition(RoutingSafetyGateState.SwitchCompleted,
                        $"Switch completed successfully to {switchResult.TargetEmail}");
                }
                else if (_safetyGate.State != RoutingSafetyGateState.Idle &&
                         _safetyGate.State != RoutingSafetyGateState.Cooldown)
                    _safetyGate.Reset("Completed automatic transaction after manual contention.");
                if (_safetyGate.State != RoutingSafetyGateState.Cooldown)
                    _safetyGate.Transition(RoutingSafetyGateState.Cooldown,
                        "Stabilizing after successful automatic switch");
                _cooldownUntil = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(30);
                _lastDecisionReason = $"Switch succeeded to {switchResult.TargetEmail}.";
                return;
            }

            if (switchResult.Code == SwitchResultCodes.SwitchFailedRolledBack)
            {
                _candidateCooldowns[targetAccountId] = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(60);
                if (!_manualSwitchPending)
                {
                    if (_safetyGate.State == RoutingSafetyGateState.SwitchInProgress)
                        _safetyGate.Transition(RoutingSafetyGateState.SwitchFailed,
                            $"Switch failed and was rolled back: {switchResult.Message}");
                    else if (_safetyGate.State != RoutingSafetyGateState.Idle &&
                             _safetyGate.State != RoutingSafetyGateState.Cooldown)
                        _safetyGate.Reset("Rolled back automatic transaction after contention.");
                    if (_safetyGate.State != RoutingSafetyGateState.Cooldown)
                        _safetyGate.Transition(RoutingSafetyGateState.Cooldown,
                            "Stabilizing after rolled back switch");
                    _cooldownUntil = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(30);
                }
                _lastDecisionReason = $"Switch failed and was rolled back to {switchResult.PreviousEmail}.";
                return;
            }
            if (!_manualSwitchPending && _safetyGate.State == RoutingSafetyGateState.SwitchInProgress)
                _safetyGate.Reset($"Switch deferred: {switchResult.Message}");
            _lastDecisionReason = $"Switch deferred: {switchResult.Message}";
            }
        }
        catch (Exception ex) when (switchResult.Success)
        {
            lock (_stateLock)
            {
                if (_lastSuccessfulManualEpoch > planEpoch) return;
                if (_safetyGate.State == RoutingSafetyGateState.ManualRecoveryRequired) return;
                if (_safetyGate.State != RoutingSafetyGateState.Idle &&
                    _safetyGate.State != RoutingSafetyGateState.ManualRecoveryRequired)
                    _safetyGate.Reset("Automatic result could not be reconciled with active identity.");
                if (_safetyGate.State != RoutingSafetyGateState.ManualRecoveryRequired)
                    _safetyGate.Transition(RoutingSafetyGateState.ManualRecoveryRequired,
                        "Automatic result ownership check failed; manual recovery required.");
                _config = _config with { AutoSwitchEnabled = false };
                _lastDecisionReason = $"Automatic switch result could not be reconciled: {AG2Security.RedactSensitiveText(ex.Message)}";
            }
        }
        finally
        {
            if (publicationLease != null) await publicationLease.DisposeAsync().ConfigureAwait(false);
            if (publicationLockHeld) publicationLock!.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _evaluatingGate.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
