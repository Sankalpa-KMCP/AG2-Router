using AG2Router.Core.Models;

namespace AG2Router.Core.Contracts;

/// <summary>
/// Opaque token identifying an in-flight manual switch operation.
/// </summary>
/// <param name="Id">Unique sequential token identifier.</param>
public readonly record struct ManualSwitchToken(long Id);

/// <summary>
/// Core contract for the native automatic routing engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Responsibilities:</b>
/// Evaluates candidate accounts against quota observations, manages stabilization cooldowns,
/// coordinates auto-switching admission, tracks manual switch epochs, and maintains configuration generations.
/// </para>
/// </remarks>
public interface INativeAutoRouter : IRouterState, IAsyncDisposable
{
    /// <summary>
    /// Executes one evaluation cycle to determine whether an account switch is required.
    /// </summary>
    Task<SelectionResult> EvaluateCycleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current router configuration snapshot.
    /// </summary>
    RouterConfigDto GetConfig();

    /// <summary>
    /// Updates and persists the router configuration.
    /// </summary>
    RouterConfigDto UpdateConfig(RouterConfigDto updates);

    /// <summary>
    /// Asynchronously updates and persists the router configuration.
    /// </summary>
    Task<RouterConfigDto> UpdateConfigAsync(RouterConfigDto updates, CancellationToken cancellationToken = default) =>
        Task.FromResult(UpdateConfig(updates));

    /// <summary>
    /// Monotonically increasing configuration generation ID (R05). Incremented on every configuration mutation.
    /// </summary>
    long ConfigGeneration => 0;

    /// <summary>
    /// Updates configuration and returns the new configuration alongside its monotonic generation ID (R05).
    /// </summary>
    (RouterConfigDto Config, long Generation) UpdateConfigWithGeneration(RouterConfigDto updates) => (UpdateConfig(updates), ConfigGeneration);

    /// <summary>
    /// Asynchronously updates configuration and returns the new configuration alongside its monotonic generation ID (R05).
    /// </summary>
    Task<(RouterConfigDto Config, long Generation)> UpdateConfigWithGenerationAsync(RouterConfigDto updates, CancellationToken cancellationToken = default) =>
        Task.FromResult(UpdateConfigWithGeneration(updates));

    /// <summary>
    /// Retrieves candidate quota evidence status for all candidate accounts without probing live Antigravity.
    /// </summary>
    Task<CandidateEvidenceStatusDto> GetCandidateEvidenceStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CandidateEvidenceStatusDto(null, GetConfig().MinimumCandidateQuotaPercent, false, []));

    /// <summary>
    /// Clears the operator manual-recovery state if the router entered ManualRecoveryRequired.
    /// </summary>
    void ResetManualRecovery();

    /// <summary>
    /// Notifies the router that an operator initiated a manual switch, cancelling any pending automatic switch.
    /// </summary>
    ManualSwitchToken NotifyManualSwitchStarted(string targetAccountId);

    /// <summary>
    /// Reconciles manual switch completion with the router's active identity and stabilization cooldowns.
    /// </summary>
    Task NotifyManualSwitchCompletedAsync(ManualSwitchToken token, NativeSwitchResult result);
}

/// <summary>
/// Evaluates whether an account switch is currently admissible based on present evidence,
/// performing strictly read-only checks with zero mutations.
/// </summary>
public interface ISwitchPlanner
{
    Task<SwitchPlanResultDto> PlanSwitchAsync(string targetAccountId, CancellationToken cancellationToken = default);
}
