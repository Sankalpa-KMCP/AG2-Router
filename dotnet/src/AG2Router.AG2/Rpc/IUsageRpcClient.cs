namespace AG2Router.AG2.Rpc;

/// <summary>
/// Narrow RPC surface for usage collection: trajectory listing for change gating, per-cascade
/// generator metadata for call accounting, and user-status observation for account attribution.
/// Implemented by <see cref="AG2RpcClient"/>; implementations must never log raw response
/// bodies and must treat transport failures as sanitized exceptions.
/// </summary>
public interface IUsageRpcClient
{
    Task<RawTrajectoriesResponse?> GetAllCascadeTrajectoriesAsync(
        int port, string protocol, string csrfToken, CancellationToken cancellationToken = default);

    Task<RawUserStatusResponse?> GetUserStatusAsync(
        int port, string protocol, string csrfToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches per-call generator metadata for one cascade conversation. Callers extract only
    /// the accounting subset via <see cref="UsageRpcPayloads.ExtractEntries(System.Text.Json.JsonDocument)"/>.
    /// </summary>
    Task<IReadOnlyList<RawGeneratorMetadataEntry>> GetCascadeTrajectoryGeneratorMetadataAsync(
        int port, string protocol, string csrfToken, string cascadeId, CancellationToken cancellationToken = default);
}
