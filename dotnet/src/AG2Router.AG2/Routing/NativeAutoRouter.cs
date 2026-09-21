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
    private readonly SemaphoreSlim _evaluatingGate = new(1, 1);
    private readonly Dictionary<string, DateTime> _candidateCooldowns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _observedQuotas = new(StringComparer.Ordinal);
    private readonly object _stateLock = new();

    private RouterConfigDto _config;
    private DateTime? _cooldownUntil;
    private string? _lastActiveAccountId;
    private string? _lastActiveAccountEmail;
    private string? _lastEvaluatedAt;
    private string _lastDecisionReason = "Router initialized";
    private CancellationTokenSource? _timerCts;
    private Task? _timerTask;
    private bool _disposed;

    public NativeAutoRouter(
        IAccountStore accountStore,
        ISessionVault sessionVault,
        IAG2Adapter adapter,
        INativeAccountSwitchCoordinator switchCoordinator,
        RouterConfigDto? initialConfig = null,
        RoutingSafetyGate? safetyGate = null)
    {
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        _sessionVault = sessionVault ?? throw new ArgumentNullException(nameof(sessionVault));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _switchCoordinator = switchCoordinator ?? throw new ArgumentNullException(nameof(switchCoordinator));
        _config = initialConfig ?? new RouterConfigDto();
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
            _config = updates;
            if (!_config.AutoSwitchEnabled &&
                (_safetyGate.State is RoutingSafetyGateState.SwitchPending or RoutingSafetyGateState.WaitingForIdle or RoutingSafetyGateState.LowQuotaDetected))
            {
                _safetyGate.Reset("Auto-switch disabled by configuration update.");
            }
            return _config;
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

            // Calculate current account usable quota fraction
            double? currentQuotaFraction = null;
            if (quotaSnapshot?.Models != null && quotaSnapshot.Models.Count > 0)
            {
                var validModels = quotaSnapshot.Models.Where(m => !m.IsExhausted).ToList();
                if (validModels.Count > 0)
                {
                    currentQuotaFraction = validModels.Min(m => m.RemainingFraction);
                }
                else
                {
                    currentQuotaFraction = 0.0;
                }
            }

            Dictionary<string, double> observedSnapshot;
            RouterConfigDto configSnapshot;
            lock (_stateLock)
            {
                if (activeAccountId != null && currentQuotaFraction.HasValue)
                {
                    _observedQuotas[activeAccountId] = currentQuotaFraction.Value;
                }
                observedSnapshot = new Dictionary<string, double>(_observedQuotas, StringComparer.Ordinal);
                configSnapshot = _config;
            }

            var accountQuotas = new Dictionary<string, double>(observedSnapshot, StringComparer.Ordinal);
            foreach (var account in accounts)
            {
                if (!accountQuotas.ContainsKey(account.Id))
                {
                    accountQuotas[account.Id] = 1.0;
                }
            }

            var vaultedAccountIds = (await _sessionVault.ListStoredAccountIdsAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

            var selection = CandidateSelector.SelectBestCandidate(
                activeAccountId,
                currentQuotaFraction,
                accounts,
                accountQuotas,
                configSnapshot,
                vaultedAccountIds,
                activeCooldownIds
            );

            lock (_stateLock)
            {
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

                            await ExecuteSwitchAsync(selection.BestCandidate.Account.Id, cancellationToken).ConfigureAwait(false);
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

    private async Task ExecuteSwitchAsync(string targetAccountId, CancellationToken cancellationToken)
    {
        NativeSwitchResult switchResult;
        try
        {
            switchResult = await _switchCoordinator.SwitchAsync(targetAccountId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _safetyGate.Transition(RoutingSafetyGateState.SwitchFailed, $"Switch invocation threw: {AG2Security.RedactSensitiveText(ex.Message)}");
            _candidateCooldowns[targetAccountId] = DateTime.UtcNow.AddSeconds(60);
            _safetyGate.Transition(RoutingSafetyGateState.Cooldown, "Stabilizing after failed switch attempt");
            _cooldownUntil = DateTime.UtcNow.AddSeconds(30);
            return;
        }

        if (switchResult.Success)
        {
            _safetyGate.Transition(RoutingSafetyGateState.Verifying, $"Verifying target identity {switchResult.TargetEmail}");
            _safetyGate.Transition(RoutingSafetyGateState.SwitchCompleted, $"Switch completed successfully to {switchResult.TargetEmail}");
            _safetyGate.Transition(RoutingSafetyGateState.Cooldown, "Stabilizing after successful switch");
            _cooldownUntil = DateTime.UtcNow.AddSeconds(30);
            lock (_stateLock)
            {
                _lastDecisionReason = $"Switch succeeded to {switchResult.TargetEmail}.";
            }
        }
        else if (switchResult.ManualRecoveryRequired)
        {
            _safetyGate.Transition(RoutingSafetyGateState.SwitchFailed, $"Switch failed: {switchResult.Message}");
            _safetyGate.Transition(RoutingSafetyGateState.ManualRecoveryRequired, $"Manual recovery required: {switchResult.Message}");
            lock (_stateLock)
            {
                // Trip circuit breaker
                _config = _config with { AutoSwitchEnabled = false };
                _lastDecisionReason = $"Automatic switching halted: manual recovery required ({switchResult.Message}).";
            }
        }
        else if (switchResult.Code == SwitchResultCodes.SwitchFailedRolledBack)
        {
            _safetyGate.Transition(RoutingSafetyGateState.SwitchFailed, $"Switch failed and was rolled back: {switchResult.Message}");
            _candidateCooldowns[targetAccountId] = DateTime.UtcNow.AddSeconds(60);
            _safetyGate.Transition(RoutingSafetyGateState.Cooldown, "Stabilizing after rolled back switch");
            _cooldownUntil = DateTime.UtcNow.AddSeconds(30);
            lock (_stateLock)
            {
                _lastDecisionReason = $"Switch failed and was rolled back to {switchResult.PreviousEmail}.";
            }
        }
        else
        {
            _safetyGate.Reset($"Switch deferred: {switchResult.Message}");
            lock (_stateLock)
            {
                _lastDecisionReason = $"Switch deferred: {switchResult.Message}";
            }
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
