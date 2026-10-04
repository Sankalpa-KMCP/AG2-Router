using AG2Router.Core.Models;

namespace AG2Router.AG2.Routing;

public class InvalidStateTransitionException : Exception
{
    public InvalidStateTransitionException(string from, string to)
        : base($"Illegal safety gate state transition from '{from}' to '{to}'.")
    {
    }
}

/// <summary>
/// Owns the routing-progression state machine: idle → low-quota detection → pending →
/// waiting-for-idle → switching → verification → cooldown/manual-recovery. This gate is
/// deliberately separate from the switch-transaction state machine owned by
/// NativeAccountSwitchCoordinator: the router decides whether and when to request a
/// switch, while the coordinator owns the transaction itself. The two must not be
/// collapsed or merged in code or UI.
/// </summary>
public class RoutingSafetyGate
{
    // The transition table is the safety contract, not convenience: an illegal transition
    // throws instead of being coerced, so a logic error cannot silently skip a blocking
    // state. Note the deliberate asymmetries — Cooldown and ManualRecoveryRequired can
    // only exit to Idle (cooldown expiry / explicit operator reset), and there is no
    // transition that bypasses Verification on the success path.
    private static readonly Dictionary<string, HashSet<string>> ValidTransitions = new(StringComparer.OrdinalIgnoreCase)
    {
        [RoutingSafetyGateState.Idle] = new(StringComparer.OrdinalIgnoreCase)
        {
            RoutingSafetyGateState.LowQuotaDetected,
            RoutingSafetyGateState.SwitchPending,
            RoutingSafetyGateState.Cooldown,
            RoutingSafetyGateState.ManualRecoveryRequired
        },
        [RoutingSafetyGateState.LowQuotaDetected] = new(StringComparer.OrdinalIgnoreCase)
        {
            RoutingSafetyGateState.Idle,
            RoutingSafetyGateState.SwitchPending
        },
        [RoutingSafetyGateState.SwitchPending] = new(StringComparer.OrdinalIgnoreCase)
        {
            RoutingSafetyGateState.Idle,
            RoutingSafetyGateState.WaitingForIdle,
            RoutingSafetyGateState.SwitchInProgress
        },
        [RoutingSafetyGateState.WaitingForIdle] = new(StringComparer.OrdinalIgnoreCase)
        {
            RoutingSafetyGateState.Idle,
            RoutingSafetyGateState.WaitingForIdle,
            RoutingSafetyGateState.SwitchInProgress
        },
        [RoutingSafetyGateState.SwitchInProgress] = new(StringComparer.OrdinalIgnoreCase)
        {
            RoutingSafetyGateState.Verifying,
            RoutingSafetyGateState.SwitchFailed,
            RoutingSafetyGateState.Idle
        },
        [RoutingSafetyGateState.Verifying] = new(StringComparer.OrdinalIgnoreCase)
        {
            RoutingSafetyGateState.SwitchCompleted,
            RoutingSafetyGateState.SwitchFailed
        },
        [RoutingSafetyGateState.SwitchCompleted] = new(StringComparer.OrdinalIgnoreCase)
        {
            RoutingSafetyGateState.Cooldown,
            RoutingSafetyGateState.Idle
        },
        [RoutingSafetyGateState.SwitchFailed] = new(StringComparer.OrdinalIgnoreCase)
        {
            RoutingSafetyGateState.Cooldown,
            RoutingSafetyGateState.ManualRecoveryRequired,
            RoutingSafetyGateState.Idle
        },
        [RoutingSafetyGateState.Cooldown] = new(StringComparer.OrdinalIgnoreCase)
        {
            RoutingSafetyGateState.Idle
        },
        [RoutingSafetyGateState.ManualRecoveryRequired] = new(StringComparer.OrdinalIgnoreCase)
        {
            RoutingSafetyGateState.Idle
        }
    };

    private readonly object _gateLock = new();
    private string _state = RoutingSafetyGateState.Idle;
    private string _lastTransitionReason = "Initialized";
    private string? _targetAccountId;

    public string State
    {
        get { lock (_gateLock) return _state; }
    }

    public string? TargetAccountId
    {
        get { lock (_gateLock) return _targetAccountId; }
    }

    public string LastTransitionReason
    {
        get { lock (_gateLock) return _lastTransitionReason; }
    }

    public SafetyGateAssessment AssessActivity(ActivityStatusDto? activity)
    {
        lock (_gateLock)
        {
            // Fail-closed: unavailable activity and UNKNOWN states look identical to this
            // assessment — neither may ever be treated as IDLE. Only an explicitly observed
            // IDLE state with zero running trajectories can authorize proceeding.
            if (activity == null)
            {
                return new SafetyGateAssessment(
                    CanProceed: false,
                    CurrentState: _state,
                    Reason: "Activity telemetry is unavailable. Must be confirmed IDLE before proceeding.",
                    ActiveTrajectoriesCount: 0
                );
            }

            if (activity.RunningTrajectories > 0 || string.Equals(activity.State, "BUSY", StringComparison.OrdinalIgnoreCase))
            {
                return new SafetyGateAssessment(
                    CanProceed: false,
                    CurrentState: _state,
                    Reason: $"Antigravity 2 is actively executing tasks ({activity.RunningTrajectories} running trajectories). Switching is blocked to prevent data loss.",
                    ActiveTrajectoriesCount: activity.RunningTrajectories
                );
            }

            if (string.Equals(activity.State, "IDLE", StringComparison.OrdinalIgnoreCase) && activity.RunningTrajectories == 0)
            {
                return new SafetyGateAssessment(
                    CanProceed: true,
                    CurrentState: _state,
                    Reason: "Antigravity 2 is IDLE with 0 running trajectories. Safe for switching.",
                    ActiveTrajectoriesCount: 0
                );
            }

            return new SafetyGateAssessment(
                CanProceed: false,
                CurrentState: _state,
                Reason: $"Antigravity 2 activity state is '{activity.State}'. Must be confirmed IDLE before proceeding.",
                ActiveTrajectoriesCount: activity.RunningTrajectories
            );
        }
    }

    /// <summary>
    /// Transitions the gate to <paramref name="to"/>, throwing
    /// <see cref="InvalidStateTransitionException"/> if the move is not in the table.
    /// The last transition reason is retained for diagnostics and dashboard display.
    /// </summary>
    public void Transition(string to, string reason, string? targetAccountId = null)
    {
        lock (_gateLock)
        {
            if (!ValidTransitions.TryGetValue(_state, out var allowed) || !allowed.Contains(to))
            {
                throw new InvalidStateTransitionException(_state, to);
            }

            _state = to;
            _lastTransitionReason = reason;
            if (targetAccountId != null)
            {
                _targetAccountId = targetAccountId;
            }
            // Returning to Idle clears the target binding so a stale account id can never
            // be read as the gate's current intent after the routing cycle completes.
            if (string.Equals(to, RoutingSafetyGateState.Idle, StringComparison.OrdinalIgnoreCase))
            {
                _targetAccountId = null;
            }
        }
    }

    /// <summary>
    /// Bypasses the transition table and returns this gate to Idle. Reset is used for flow
    /// teardown (for example, auto-switch being disabled by configuration or a manual switch
    /// superseding a pending plan), for outcome handling that defers persistent blocking to
    /// the router-level manual-recovery state, and by the operator reset-recovery flow.
    /// Persistent manual-recovery blocking is enforced by the router's recovery state, not
    /// by this gate alone.
    /// </summary>
    public void Reset(string reason = "Reset to IDLE")
    {
        lock (_gateLock)
        {
            _state = RoutingSafetyGateState.Idle;
            _targetAccountId = null;
            _lastTransitionReason = reason;
        }
    }
}
