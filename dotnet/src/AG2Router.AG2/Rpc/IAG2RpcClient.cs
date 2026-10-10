namespace AG2Router.AG2.Rpc;

public enum ProbeFailureReason
{
    None,
    InvalidEndpoint,
    ProbeTimeout,
    RejectedRedirect,
    ServerError,
    ProtocolMismatch,
    IncompleteResponseBody,
    TlsHandshakeFailure,
    TransportError,
    Cancelled,
    Unknown
}

public sealed record ProbeResult(bool Success, ProbeFailureReason FailureReason, int StatusCode = 0);

public interface IAG2RpcClient
{
    /// <summary>
    /// Probes a candidate port and protocol to determine if an active Connect-RPC LanguageServerService is listening.
    /// <para>
    /// Returns true if the port responds with status code &lt; 500, response body inspection cleanly completes,
    /// and does not indicate an HTTP-to-HTTPS protocol mismatch. Rejects redirects (3xx) and server errors (5xx).
    /// </para>
    /// <para>
    /// Protocol detection is a bounded-prefix heuristic: only relevant 4xx response bodies undergo bounded mismatch inspection,
    /// completing at EOF or the 512-application-byte cutoff. Known mismatch signatures in the inspected prefix are rejected;
    /// content outside that prefix is not examined. Genuine generic application 400 responses without mismatch signatures
    /// remain compatible and indicate reachability. Probe success is evidence of reachability, not proof of complete RPC correctness.
    /// </para>
    /// </summary>
    Task<bool> ProbePortAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a detailed probe returning failure classification for diagnostics.
    /// Default implementation adapts a Boolean-only <see cref="ProbePortAsync"/> implementation via straightforward await,
    /// preserving task completion, cancellation, and original fault semantics without AggregateException wrapping.
    /// Unclassified false outcomes from Boolean adapters are categorized as <see cref="ProbeFailureReason.Unknown"/>.
    /// </summary>
    async Task<ProbeResult> ProbePortDetailedAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        bool success = await ProbePortAsync(port, protocol, csrfToken, cancellationToken).ConfigureAwait(false);
        return new ProbeResult(success, success ? ProbeFailureReason.None : ProbeFailureReason.Unknown);
    }

    /// <summary>
    /// Invokes GetUserStatus to retrieve current account identity and model quotas.
    /// </summary>
    Task<RawUserStatusResponse?> GetUserStatusAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes GetAllCascadeTrajectories to retrieve active trajectory summaries and run statuses.
    /// </summary>
    Task<RawTrajectoriesResponse?> GetAllCascadeTrajectoriesAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default);
}
