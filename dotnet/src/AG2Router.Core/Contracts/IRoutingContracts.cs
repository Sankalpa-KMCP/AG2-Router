using AG2Router.Core.Models;

namespace AG2Router.Core.Contracts;

public interface INativeAutoRouter : IRouterState, IAsyncDisposable
{
    Task<SelectionResult> EvaluateCycleAsync(CancellationToken cancellationToken = default);
    RouterConfigDto GetConfig();
    RouterConfigDto UpdateConfig(RouterConfigDto updates);
    void ResetManualRecovery();
}
