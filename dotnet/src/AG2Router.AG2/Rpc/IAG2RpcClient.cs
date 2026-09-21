namespace AG2Router.AG2.Rpc;

public interface IAG2RpcClient
{
    /// <summary>
    /// Probes a candidate port and protocol to determine if an active Connect-RPC LanguageServerService is listening.
    /// Returns true if the port responds with status code < 500.
    /// </summary>
    Task<bool> ProbePortAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes GetUserStatus to retrieve current account identity and model quotas.
    /// </summary>
    Task<RawUserStatusResponse?> GetUserStatusAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes GetAllCascadeTrajectories to retrieve active trajectory summaries and run statuses.
    /// </summary>
    Task<RawTrajectoriesResponse?> GetAllCascadeTrajectoriesAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default);
}
