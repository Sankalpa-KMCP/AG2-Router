using System;
using System.Threading;
using System.Threading.Tasks;
using AG2Router.AG2.Routing;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Switching;

/// <summary>
/// Read-only switch readiness planner evaluating whether Antigravity 2 can safely switch
/// to a target account based on present evidence, with zero side effects or mutations.
/// </summary>
public class SwitchPlanner : ISwitchPlanner
{
    private readonly IAccountStore _accountStore;
    private readonly ISessionVault _sessionVault;
    private readonly IAG2Adapter _adapter;
    private readonly INativeAccountSwitchCoordinator _switchCoordinator;
    private readonly INativeAutoRouter? _autoRouter;
    private readonly IQuotaObservationStore? _quotaObservationStore;
    private readonly TimeProvider _timeProvider;

    public SwitchPlanner(
        IAccountStore accountStore,
        ISessionVault sessionVault,
        IAG2Adapter adapter,
        INativeAccountSwitchCoordinator switchCoordinator,
        INativeAutoRouter? autoRouter = null,
        IQuotaObservationStore? quotaObservationStore = null,
        TimeProvider? timeProvider = null)
    {
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        _sessionVault = sessionVault ?? throw new ArgumentNullException(nameof(sessionVault));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _switchCoordinator = switchCoordinator ?? throw new ArgumentNullException(nameof(switchCoordinator));
        _autoRouter = autoRouter;
        _quotaObservationStore = quotaObservationStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Evaluates switch admissibility for a requested target account.
    /// Strictly read-only: performs zero credential writes, zero process signals, and zero admissions.
    /// </summary>
    public async Task<SwitchPlanResultDto> PlanSwitchAsync(
        string targetAccountId,
        CancellationToken cancellationToken = default)
    {
        // 1. Target ID presence check
        if (string.IsNullOrWhiteSpace(targetAccountId))
        {
            return new SwitchPlanResultDto(
                TargetAccountId: targetAccountId ?? string.Empty,
                TargetEmail: null,
                TargetExists: false,
                IsEligible: false,
                HasVaultedSession: false,
                IsAlreadyActive: false,
                IsReserve: false,
                SystemState: "UNKNOWN",
                SafetyState: "UNKNOWN",
                WorkloadModelKey: null,
                TargetQuotaPercent: null,
                QuotaStatus: "UNKNOWN",
                ObservationAgeSeconds: null,
                InCooldown: false,
                IneligibilityReason: "Target account was not specified.",
                Admissible: false,
                ReasonCode: SwitchPlanReasonCodes.TargetNotFound,
                Message: "Target account was not specified."
            );
        }

        // 2. Target existence check
        AccountMetadata? target = await _accountStore.GetAccountAsync(targetAccountId, cancellationToken).ConfigureAwait(false);
        if (target == null)
        {
            return new SwitchPlanResultDto(
                TargetAccountId: targetAccountId,
                TargetEmail: null,
                TargetExists: false,
                IsEligible: false,
                HasVaultedSession: false,
                IsAlreadyActive: false,
                IsReserve: false,
                SystemState: "UNKNOWN",
                SafetyState: "UNKNOWN",
                WorkloadModelKey: null,
                TargetQuotaPercent: null,
                QuotaStatus: "UNKNOWN",
                ObservationAgeSeconds: null,
                InCooldown: false,
                IneligibilityReason: "Target account was not found in storage.",
                Admissible: false,
                ReasonCode: SwitchPlanReasonCodes.TargetNotFound,
                Message: $"Target account '{targetAccountId}' was not found."
            );
        }

        string targetEmail = target.Email;
        bool isReserve = target.IsReserve;

        // 3. Vaulted session presence check (only checks presence; never decrypts session material)
        bool hasVaultedSession = await _sessionVault.HasSessionAsync(target.Id, cancellationToken).ConfigureAwait(false);

        // 4. Target eligibility & validation status
        bool isEligible = true;
        string? ineligibilityReason = null;
        if (string.Equals(target.ValidationStatus, AccountValidationStatus.Expired, StringComparison.OrdinalIgnoreCase))
        {
            isEligible = false;
            ineligibilityReason = "Target account validation status is EXPIRED.";
        }
        else if (string.Equals(target.ValidationStatus, AccountValidationStatus.Failed, StringComparison.OrdinalIgnoreCase))
        {
            isEligible = false;
            ineligibilityReason = "Target account validation status is FAILED.";
        }

        // 5. Active account check from store and metadata
        string? activeId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
        AccountMetadata? activeAccount = activeId == null ? null :
            await _accountStore.GetAccountAsync(activeId, cancellationToken).ConfigureAwait(false);
        bool isAlreadyActive = string.Equals(activeId, target.Id, StringComparison.Ordinal) ||
                               string.Equals(activeAccount?.Email, target.Email, StringComparison.OrdinalIgnoreCase);

        // 6. Coordinator admission & safety check
        bool canAdmitCoordinator = _switchCoordinator.CanAdmitSwitch(out string? coordinatorBlockReason);
        var coordStatus = _switchCoordinator.GetStatus();

        string safetyState = "IDLE";
        if (coordStatus.QuarantineActive)
        {
            safetyState = "QUARANTINED";
        }
        else if (!string.Equals(coordStatus.JournalRecoveryState, JournalRecoveryStates.None, StringComparison.Ordinal))
        {
            safetyState = $"RECOVERY_{coordStatus.JournalRecoveryState}";
        }
        else if (coordStatus.ActiveTransactionId != null || !string.Equals(coordStatus.CurrentState, NativeSwitchStates.Idle, StringComparison.Ordinal))
        {
            safetyState = "SWITCH_IN_PROGRESS";
        }

        // 7. AutoRouter safety & cooldown check (if configured)
        bool inCooldown = false;
        if (_autoRouter != null)
        {
            var routerStatus = _autoRouter.GetStatus();
            if (routerStatus.State == RoutingSafetyGateState.Cooldown)
            {
                inCooldown = true;
                safetyState = "COOLDOWN";
            }
            else if (routerStatus.State == RoutingSafetyGateState.ManualRecoveryRequired)
            {
                safetyState = "MANUAL_RECOVERY_REQUIRED";
            }
        }

        // 8. Live Antigravity telemetry and activity
        Ag2StatusDto? ag2Status = null;
        ActivityStatusDto? activity = null;
        AccountIdentityDto? liveIdentity = null;
        string systemState = "UNKNOWN";

        try
        {
            ag2Status = await _adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            liveIdentity = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            activity = await _adapter.GetActivityStateAsync(cancellationToken).ConfigureAwait(false);
            systemState = activity?.State ?? (ag2Status.Connected ? "ONLINE" : "OFFLINE");
            if (liveIdentity != null && string.Equals(liveIdentity.Email, target.Email, StringComparison.OrdinalIgnoreCase))
            {
                isAlreadyActive = true;
            }
        }
        catch
        {
            systemState = "UNREACHABLE";
        }

        // 9. Quota evidence evaluation for configured workload model
        var now = _timeProvider.GetUtcNow();
        var routerConfig = _autoRouter?.GetConfig();
        string? workloadModelKey = CandidateSelector.CanonicalizeModelKey(routerConfig?.WorkloadModelKey);
        int minCandidateQuotaPercent = routerConfig?.MinimumCandidateQuotaPercent ?? 30;

        AccountModelQuotaObservation? observation = null;
        if (_quotaObservationStore != null && workloadModelKey != null)
        {
            try
            {
                observation = await _quotaObservationStore.GetObservationAsync(target.Id, workloadModelKey, cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }

        string quotaStatus = "UNCONFIGURED";
        double? targetQuotaPercent = null;
        double? observationAgeSeconds = null;

        if (workloadModelKey == null)
        {
            quotaStatus = "UNCONFIGURED";
        }
        else if (observation == null)
        {
            quotaStatus = "NOT_OBSERVED";
        }
        else
        {
            observationAgeSeconds = Math.Max(0, (now - observation.ObservedAtUtc).TotalSeconds);
            if (observation.ObservedAtUtc > now)
            {
                quotaStatus = "INVALID";
            }
            else if (now - observation.ObservedAtUtc >= CandidateSelector.InactiveCandidateEvidenceLifetime)
            {
                quotaStatus = "STALE";
            }
            else if (observation.RemainingFraction == null || !double.IsFinite(observation.RemainingFraction.Value))
            {
                quotaStatus = "UNKNOWN";
            }
            else if (observation.RemainingFraction <= 0)
            {
                quotaStatus = "EXHAUSTED";
                targetQuotaPercent = 0.0;
            }
            else
            {
                targetQuotaPercent = Math.Round(observation.RemainingFraction.Value * 100.0, 1);
                quotaStatus = QuotaObservationEvidence.IsUsable(observation, now, minCandidateQuotaPercent)
                    ? "USABLE"
                    : "BELOW_MINIMUM";
            }
        }

        // 10. Sequential Rule Evaluation
        // Rule 1: Account Eligibility
        if (!isEligible)
        {
            return new SwitchPlanResultDto(
                target.Id, targetEmail, true, false, hasVaultedSession, isAlreadyActive, isReserve,
                systemState, safetyState, workloadModelKey, targetQuotaPercent, quotaStatus, observationAgeSeconds,
                inCooldown, ineligibilityReason, false, SwitchPlanReasonCodes.TargetIneligible,
                ineligibilityReason ?? "Target account is ineligible."
            );
        }

        // Rule 2: Vaulted Session
        if (!hasVaultedSession)
        {
            return new SwitchPlanResultDto(
                target.Id, targetEmail, true, isEligible, false, isAlreadyActive, isReserve,
                systemState, safetyState, workloadModelKey, targetQuotaPercent, quotaStatus, observationAgeSeconds,
                inCooldown, ineligibilityReason, false, SwitchPlanReasonCodes.TargetNotVaulted,
                "Target account does not have a valid vaulted session."
            );
        }

        // Rule 3: Already Active
        if (isAlreadyActive)
        {
            return new SwitchPlanResultDto(
                target.Id, targetEmail, true, isEligible, hasVaultedSession, true, isReserve,
                systemState, safetyState, workloadModelKey, targetQuotaPercent, quotaStatus, observationAgeSeconds,
                inCooldown, ineligibilityReason, false, SwitchPlanReasonCodes.AlreadyActive,
                "Target account is already active."
            );
        }

        // Rule 4: Coordinator Admission Gate
        if (!canAdmitCoordinator)
        {
            string code = coordStatus.ActiveTransactionId != null || !string.Equals(coordStatus.CurrentState, NativeSwitchStates.Idle, StringComparison.Ordinal)
                ? SwitchPlanReasonCodes.SwitchInProgress
                : SwitchPlanReasonCodes.SafetyBlocked;
            return new SwitchPlanResultDto(
                target.Id, targetEmail, true, isEligible, hasVaultedSession, isAlreadyActive, isReserve,
                systemState, safetyState, workloadModelKey, targetQuotaPercent, quotaStatus, observationAgeSeconds,
                inCooldown, ineligibilityReason, false, code,
                coordinatorBlockReason ?? "Switch is blocked by coordinator admission gate."
            );
        }

        // Rule 5: Router Cooldown / Safety Gate
        if (inCooldown)
        {
            return new SwitchPlanResultDto(
                target.Id, targetEmail, true, isEligible, hasVaultedSession, isAlreadyActive, isReserve,
                systemState, safetyState, workloadModelKey, targetQuotaPercent, quotaStatus, observationAgeSeconds,
                true, ineligibilityReason, false, SwitchPlanReasonCodes.CooldownActive,
                "Router is currently in stabilization cooldown."
            );
        }
        if (safetyState == "MANUAL_RECOVERY_REQUIRED")
        {
            return new SwitchPlanResultDto(
                target.Id, targetEmail, true, isEligible, hasVaultedSession, isAlreadyActive, isReserve,
                systemState, safetyState, workloadModelKey, targetQuotaPercent, quotaStatus, observationAgeSeconds,
                false, ineligibilityReason, false, SwitchPlanReasonCodes.SafetyBlocked,
                "Router requires manual recovery before switching can be admitted."
            );
        }

        // Rule 6: Antigravity Telemetry Connectivity & Identity Coherence
        if (ag2Status == null || !ag2Status.Connected || ag2Status.Status is "DEGRADED" or "OFFLINE" or "ERROR" || liveIdentity == null)
        {
            return new SwitchPlanResultDto(
                target.Id, targetEmail, true, isEligible, hasVaultedSession, isAlreadyActive, isReserve,
                systemState, safetyState, workloadModelKey, targetQuotaPercent, quotaStatus, observationAgeSeconds,
                inCooldown, ineligibilityReason, false, SwitchPlanReasonCodes.TelemetryUnavailable,
                "Antigravity telemetry is unavailable or degraded."
            );
        }

        if (!SourceIdentityMatches(activeAccount, liveIdentity))
        {
            return new SwitchPlanResultDto(
                target.Id, targetEmail, true, isEligible, hasVaultedSession, isAlreadyActive, isReserve,
                systemState, safetyState, workloadModelKey, targetQuotaPercent, quotaStatus, observationAgeSeconds,
                inCooldown, ineligibilityReason, false, SwitchPlanReasonCodes.TelemetryUnavailable,
                "Live identity does not match the active account metadata."
            );
        }

        // Rule 7: Antigravity Activity State (must be IDLE)
        if (activity == null || !string.Equals(activity.State, "IDLE", StringComparison.OrdinalIgnoreCase) || activity.RunningTrajectories > 0)
        {
            bool isBusy = activity != null && (activity.RunningTrajectories > 0 || string.Equals(activity.State, "BUSY", StringComparison.OrdinalIgnoreCase));
            string reasonCode = isBusy ? SwitchPlanReasonCodes.Ag2Busy : SwitchPlanReasonCodes.TelemetryUnavailable;
            string message = isBusy ? "Antigravity must be IDLE before switching." : "Antigravity activity telemetry is unknown or degraded.";
            return new SwitchPlanResultDto(
                target.Id, targetEmail, true, isEligible, hasVaultedSession, isAlreadyActive, isReserve,
                systemState, safetyState, workloadModelKey, targetQuotaPercent, quotaStatus, observationAgeSeconds,
                inCooldown, ineligibilityReason, false, reasonCode, message
            );
        }

        // Rule 8: Advisory Quota Information
        // Manual switching uses the same coordinator and safety gates without requiring
        // workload-model configuration or automatic target-quota thresholds (docs/domain-rules.md:87).
        // Quota evidence is advisory for operator visibility and does not gate manual switch admissibility.
        string planMessage = quotaStatus switch
        {
            "EXHAUSTED" => $"Target account is admissible, but quota is exhausted for model '{workloadModelKey}'.",
            "BELOW_MINIMUM" => $"Target account is admissible, but quota ({targetQuotaPercent}%) is below candidate threshold ({minCandidateQuotaPercent}%).",
            _ => "Target account is admissible for switching."
        };

        // All preflight safety checks passed! Admissible!
        return new SwitchPlanResultDto(
            TargetAccountId: target.Id,
            TargetEmail: targetEmail,
            TargetExists: true,
            IsEligible: true,
            HasVaultedSession: true,
            IsAlreadyActive: false,
            IsReserve: isReserve,
            SystemState: systemState,
            SafetyState: safetyState,
            WorkloadModelKey: workloadModelKey,
            TargetQuotaPercent: targetQuotaPercent,
            QuotaStatus: quotaStatus,
            ObservationAgeSeconds: observationAgeSeconds,
            InCooldown: false,
            IneligibilityReason: null,
            Admissible: true,
            ReasonCode: SwitchPlanReasonCodes.Success,
            Message: planMessage
        );
    }

    internal static bool SourceIdentityMatches(AccountMetadata? source, AccountIdentityDto? live) =>
        source != null && !string.IsNullOrWhiteSpace(source.Email) &&
        !string.IsNullOrWhiteSpace(live?.Email) &&
        string.Equals(source.Email.Trim(), live.Email.Trim(), StringComparison.OrdinalIgnoreCase);
}
