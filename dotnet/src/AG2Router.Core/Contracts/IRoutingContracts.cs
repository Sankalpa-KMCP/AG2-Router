using AG2Router.Core.Models;

namespace AG2Router.Core.Contracts;

public readonly record struct ManualSwitchToken(long Id);

public interface INativeAutoRouter : IRouterState, IAsyncDisposable
{
    Task<SelectionResult> EvaluateCycleAsync(CancellationToken cancellationToken = default);
    RouterConfigDto GetConfig();
    RouterConfigDto UpdateConfig(RouterConfigDto updates);
    Task<CandidateEvidenceStatusDto> GetCandidateEvidenceStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CandidateEvidenceStatusDto(null, GetConfig().MinimumCandidateQuotaPercent, false, []));
    void ResetManualRecovery();
    ManualSwitchToken NotifyManualSwitchStarted(string targetAccountId);
    Task NotifyManualSwitchCompletedAsync(ManualSwitchToken token, NativeSwitchResult result);
}
