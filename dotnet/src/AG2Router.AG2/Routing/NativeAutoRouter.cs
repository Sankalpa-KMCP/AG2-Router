using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Security;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

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
    private readonly SemaphoreSlim _evaluatingGate = new(1, 1);
    private readonly Dictionary<string, DateTime> _candidateCooldowns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _observedQuotas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ModelQuotaDto>> _observedModelQuotas = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _activeManualSwitches = new();
    private readonly object _stateLock = new();

    private RouterConfigDto _config;
    private DateTime? _cooldownUntil;
    private string? _lastActiveAccountId;
    private string? _lastActiveAccountEmail;
    private string? _lastEvaluatedAt;
    private string _lastDecisionReason = "Router initialized";
    private long _manualSwitchEpoch;
    private long _lastSuccessfulManualEpoch;
    private long _nextManualTokenId;
    private bool _manualSwitchPending;
    private CancellationTokenSource? _timerCts;
    private Task? _timerTask;
    private bool _disposed;

    public NativeAutoRouter(
        IAccountStore accountStore,
        ISessionVault sessionVault,
        IAG2Adapter adapter,
        INativeAccountSwitchCoordinator switchCoordinator,
        RouterConfigDto? initialConfig = null,
        RoutingSafetyGate? safetyGate = null,
        string? configFilePath = null)
        : this(accountStore, sessionVault, adapter, switchCoordinator, initialConfig, safetyGate, configFilePath, (!string.IsNullOrEmpty(configFilePath) ? new DurableFileWriter() : null))
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
        IDurableFileWriter? fileWriter)
    {
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        _sessionVault = sessionVault ?? throw new ArgumentNullException(nameof(sessionVault));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _switchCoordinator = switchCoordinator ?? throw new ArgumentNullException(nameof(switchCoordinator));
        _configFilePath = configFilePath;
        _fileWriter = fileWriter ?? (!string.IsNullOrEmpty(configFilePath) ? new DurableFileWriter() : null);

        RouterConfigDto? loadedConfig = null;
        if (!string.IsNullOrEmpty(_configFilePath) && File.Exists(_configFilePath))
        {
            try
            {
                var json = File.ReadAllText(_configFilePath);
                loadedConfig = JsonSerializer.Deserialize<RouterConfigDto>(json);
            }
            catch { }
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

    public RouterConfigDto UpdateConfig(RouterConfigDto updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        lock (_stateLock)
        {
            if (!string.IsNullOrEmpty(_configFilePath) && _fileWriter != null)
            {
                var json = JsonSerializer.Serialize(updates, new JsonSerializerOptions { WriteIndented = true });
                _fileWriter.WriteAtomicAsync(_configFilePath, json, CancellationToken.None).GetAwaiter().GetResult();
            }

            _config = updates;
            if (!_config.AutoSwitchEnabled &&
                (_safetyGate.State is RoutingSafetyGateState.SwitchPending or RoutingSafetyGateState.WaitingForIdle or RoutingSafetyGateState.LowQuotaDetected))
            {
                _safetyGate.Reset("Auto-switch disabled by configuration update.");
            }
            return _config;
        }
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
                _cooldownUntil = DateTime.UtcNow.AddSeconds(30);
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
            _cooldownUntil = DateTime.UtcNow.AddSeconds(30);
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

    public void SetObservedQuota(string accountId, double remainingFraction)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        lock (_stateLock)
        {
            _observedQuotas[accountId] = remainingFraction;
            if (!_observedModelQuotas.ContainsKey(accountId))
            {
                _observedModelQuotas[accountId] = new List<ModelQuotaDto>
                {
                    new("Gemini Pro", "gemini-pro", remainingFraction, null, false),
                    new("Claude 3.7 Sonnet", "claude-3-7-sonnet", remainingFraction, null, false),
                    new("Gemini", "gemini", remainingFraction, null, false)
                };
            }
        }
    }

    public void SetObservedModelQuotas(string accountId, IReadOnlyList<ModelQuotaDto> models)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        ArgumentNullException.ThrowIfNull(models);
        lock (_stateLock)
        {
            _observedModelQuotas[accountId] = models;
        }
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_timerCts != null || _disposed) return;
            _timerCts = new CancellationTokenSource();
            var token = _timerCts.Token;

            _timerTask = Task.Run(async () =>
            {
                try
                {
                    await EvaluateCycleAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // Swallowed to preserve background worker resilience
                }

                int intervalMs = Math.Max(1000, _config.PollingIntervalMs);
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                        {
                            break;
                        }
                        await EvaluateCycleAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Swallowed to prevent loop termination
                    }
                }
            }, token);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_stateLock)
        {
            cts = _timerCts;
            task = _timerTask;
            _timerCts = null;
            _timerTask = null;
        }

        if (cts != null)
        {
            cts.Cancel();
            if (task != null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch { }
            }
            cts.Dispose();
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
            string nowIso = DateTime.UtcNow.ToString("o");

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

            // Check stabilization cooldown
            if (_safetyGate.State == RoutingSafetyGateState.Cooldown)
            {
                if (_cooldownUntil.HasValue && DateTime.UtcNow < _cooldownUntil.Value)
                {
                    lock (_stateLock)
                    {
                        _lastEvaluatedAt = nowIso;
                        _lastDecisionReason = $"In cooldown until {_cooldownUntil.Value:O}.";
                    }
                    return new SelectionResult(false, $"In cooldown until {_cooldownUntil.Value:O}.", _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
                }

                // Cooldown timer elapsed: check telemetry health before returning to IDLE
                var probeStatus = await _adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (probeStatus.Connected && probeStatus.Status != "DEGRADED" && probeStatus.Status != "ERROR")
                {
                    _safetyGate.Transition(RoutingSafetyGateState.Idle, "Cooldown elapsed; returning to monitoring.");
                    _cooldownUntil = null;
                }
                else
                {
                    lock (_stateLock)
                    {
                        _lastEvaluatedAt = nowIso;
                        _lastDecisionReason = "Cooldown elapsed but telemetry remains unavailable.";
                    }
                    return new SelectionResult(false, "Cooldown elapsed but telemetry remains unavailable.", _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
                }
            }

            // Check Antigravity availability
            var ag2Status = await _adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!ag2Status.Connected || ag2Status.Status is "DEGRADED" or "OFFLINE" or "ERROR")
            {
                if (_safetyGate.State == RoutingSafetyGateState.WaitingForIdle)
                {
                    _safetyGate.Reset("Antigravity offline or disconnected; pending switch cancelled.");
                }

                lock (_stateLock)
                {
                    _lastEvaluatedAt = nowIso;
                    _lastDecisionReason = ag2Status.Message ?? "Antigravity offline or not detected.";
                }

                return new SelectionResult(false, ag2Status.Message ?? "Antigravity offline or not detected.", _lastActiveAccountId, null, null, Array.Empty<CandidateEvaluation>());
            }

            // Clean expired candidate cooldowns
            var nowUtc = DateTime.UtcNow;
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

            lock (_stateLock)
            {
                _lastActiveAccountId = activeAccountId;
                _lastActiveAccountEmail = activeAccount?.Email;
            }

            var quotaSnapshot = await _adapter.GetQuotaAsync(cancellationToken).ConfigureAwait(false);

            // Calculate current account usable quota fraction (weakest-link across models)
            double? currentQuotaFraction = null;
            if (quotaSnapshot?.Models != null && quotaSnapshot.Models.Count > 0)
            {
                if (quotaSnapshot.Models.Any(m => m.IsExhausted || (m.RemainingFraction.HasValue && m.RemainingFraction.Value <= 0.0)))
                {
                    currentQuotaFraction = 0.0;
                }
                else
                {
                    var validFractions = quotaSnapshot.Models
                        .Where(m => m.RemainingFraction.HasValue && double.IsFinite(m.RemainingFraction.Value))
                        .Select(m => m.RemainingFraction!.Value)
                        .ToList();

                    currentQuotaFraction = validFractions.Count > 0
                        ? validFractions.Min()
                        : null;
                }
            }

            Dictionary<string, double> observedSnapshot;
            Dictionary<string, IReadOnlyList<ModelQuotaDto>> observedModelSnapshot;
            RouterConfigDto configSnapshot;
            string switchResource = _sessionVault.GetVaultPath() + ".switch";
            var switchLock = PathLockRegistry.Get(switchResource);
            await switchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var lease = await CrossProcessFileLease.AcquireAsync(switchResource, cancellationToken)
                    .ConfigureAwait(false);
                var activeAfterQuota = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
                lock (_stateLock)
                {
                    if (_manualSwitchEpoch != planEpoch || _manualSwitchPending ||
                        !string.Equals(activeAfterQuota, activeAccountId, StringComparison.Ordinal))
                        return new SelectionResult(false, "Active account changed during quota observation.",
                            activeAfterQuota, null, null, Array.Empty<CandidateEvaluation>());
                    if (activeAccountId != null)
                    {
                        if (currentQuotaFraction.HasValue && double.IsFinite(currentQuotaFraction.Value))
                            _observedQuotas[activeAccountId] = currentQuotaFraction.Value;
                        else
                            _observedQuotas.Remove(activeAccountId);

                        if (quotaSnapshot?.Models != null)
                            _observedModelQuotas[activeAccountId] = quotaSnapshot.Models;
                        else
                            _observedModelQuotas.Remove(activeAccountId);
                    }
                    observedSnapshot = new Dictionary<string, double>(_observedQuotas, StringComparer.Ordinal);
                    observedModelSnapshot = new Dictionary<string, IReadOnlyList<ModelQuotaDto>>(_observedModelQuotas, StringComparer.Ordinal);
                    configSnapshot = _config;
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

            var lowThresholdFraction = configSnapshot.LowQuotaThresholdPercent / 100.0;
            var relevantModelKeys = quotaSnapshot?.Models?
                .Where(m => m.IsExhausted || (m.RemainingFraction.HasValue && m.RemainingFraction.Value <= lowThresholdFraction))
                .Select(CandidateSelector.GetModelKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var selection = CandidateSelector.SelectBestCandidate(
                activeAccountId,
                currentQuotaFraction,
                accounts,
                accountQuotas,
                configSnapshot,
                vaultedAccountIds,
                activeCooldownIds,
                observedModelSnapshot,
                relevantModelKeys
            );

            lock (_stateLock)
            {
                if (_manualSwitchEpoch != planEpoch || _manualSwitchPending || _activeManualSwitches.Count > 0)
                    return new SelectionResult(false, "Manual switch superseded evaluation.", _lastActiveAccountId,
                        null, null, Array.Empty<CandidateEvaluation>());
                _lastEvaluatedAt = nowIso;
                _lastDecisionReason = selection.Reason;
            }

            // State Machine Progression
            if (selection.ShouldSwitch && selection.BestCandidate != null)
            {
                if (_safetyGate.State == RoutingSafetyGateState.Idle)
                {
                    _safetyGate.Transition(
                        RoutingSafetyGateState.LowQuotaDetected,
                        selection.Reason,
                        selection.BestCandidate.Account.Id
                    );
                }

                if (configSnapshot.AutoSwitchEnabled)
                {
                    if (_safetyGate.State == RoutingSafetyGateState.LowQuotaDetected)
                    {
                        _safetyGate.Transition(
                            RoutingSafetyGateState.SwitchPending,
                            $"Auto-switch queued for candidate {selection.BestCandidate.Account.Email}",
                            selection.BestCandidate.Account.Id
                        );
                    }

                    var activity = await _adapter.GetActivityStateAsync(cancellationToken).ConfigureAwait(false);
                    var assessment = _safetyGate.AssessActivity(activity);

                    if (!assessment.CanProceed)
                    {
                        if (_safetyGate.State == RoutingSafetyGateState.SwitchPending)
                        {
                            _safetyGate.Transition(
                                RoutingSafetyGateState.WaitingForIdle,
                                assessment.Reason,
                                selection.BestCandidate.Account.Id
                            );
                        }
                        else if (_safetyGate.State == RoutingSafetyGateState.WaitingForIdle)
                        {
                            if (!string.Equals(_safetyGate.TargetAccountId, selection.BestCandidate.Account.Id, StringComparison.Ordinal))
                            {
                                _safetyGate.Transition(
                                    RoutingSafetyGateState.WaitingForIdle,
                                    $"Target updated to {selection.BestCandidate.Account.Email}",
                                    selection.BestCandidate.Account.Id
                                );
                            }
                        }
                    }
                    else
                    {
                        // Confirmed IDLE!
                        if (_safetyGate.State is RoutingSafetyGateState.SwitchPending or RoutingSafetyGateState.WaitingForIdle)
                        {
                            _safetyGate.Transition(
                                RoutingSafetyGateState.SwitchInProgress,
                                $"Antigravity 2 is confirmed IDLE. Executing automatic switch to {selection.BestCandidate.Account.Email}.",
                                selection.BestCandidate.Account.Id
                            );

                            await ExecuteSwitchAsync(selection.BestCandidate.Account.Id, activeAccountId, planEpoch, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            else
            {
                if (_safetyGate.State is RoutingSafetyGateState.WaitingForIdle or RoutingSafetyGateState.SwitchPending or RoutingSafetyGateState.LowQuotaDetected)
                {
                    _safetyGate.Reset("Quota conditions normalized or no candidates eligible; returning to IDLE.");
                }
            }

            return selection;
        }
        finally
        {
            _evaluatingGate.Release();
        }
    }

    private bool IsAutomaticPlanCurrent(long epoch)
    {
        lock (_stateLock)
            return !_manualSwitchPending && _activeManualSwitches.Count == 0 && _manualSwitchEpoch == epoch && _config.AutoSwitchEnabled;
    }

    private async Task ExecuteSwitchAsync(
        string targetAccountId, string? expectedActiveAccountId, long planEpoch,
        CancellationToken cancellationToken)
    {
        if (!IsAutomaticPlanCurrent(planEpoch)) return;
        NativeSwitchResult switchResult;
        try
        {
            switchResult = await _switchCoordinator.SwitchAutomaticallyAsync(
                targetAccountId, expectedActiveAccountId, () => IsAutomaticPlanCurrent(planEpoch),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                if (_lastSuccessfulManualEpoch > planEpoch) return;
                if (_manualSwitchEpoch != planEpoch)
                {
                    if (!_manualSwitchPending && _safetyGate.State == RoutingSafetyGateState.SwitchInProgress)
                        _safetyGate.Reset("Stale automatic plan threw exception; reset to idle.");
                    return;
                }
                _safetyGate.Transition(RoutingSafetyGateState.SwitchFailed, $"Switch invocation threw: {AG2Security.RedactSensitiveText(ex.Message)}");
                _candidateCooldowns[targetAccountId] = DateTime.UtcNow.AddSeconds(60);
                _safetyGate.Transition(RoutingSafetyGateState.Cooldown, "Stabilizing after failed switch attempt");
                _cooldownUntil = DateTime.UtcNow.AddSeconds(30);
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
                _cooldownUntil = DateTime.UtcNow.AddSeconds(30);
                _lastDecisionReason = $"Switch succeeded to {switchResult.TargetEmail}.";
                return;
            }

            if (switchResult.Code == SwitchResultCodes.SwitchFailedRolledBack)
            {
                _candidateCooldowns[targetAccountId] = DateTime.UtcNow.AddSeconds(60);
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
                    _cooldownUntil = DateTime.UtcNow.AddSeconds(30);
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _evaluatingGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
