using AG2Router.Core.Models;

namespace AG2Router.Core.Contracts;

public readonly record struct ManualSwitchToken(long Id);

public interface INativeAutoRouter : IRouterState, IAsyncDisposable
{
    Task<SelectionResult> EvaluateCycleAsync(CancellationToken cancellationToken = default);
    RouterConfigDto GetConfig();
    RouterConfigDto UpdateConfig(RouterConfigDto updates);
    void ResetManualRecovery();
    ManualSwitchToken NotifyManualSwitchStarted(string targetAccountId);
    Task NotifyManualSwitchCompletedAsync(ManualSwitchToken token, NativeSwitchResult result);
}
