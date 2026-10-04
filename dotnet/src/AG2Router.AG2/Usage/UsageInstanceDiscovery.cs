using AG2Router.AG2.Discovery;
using AG2Router.AG2.Rpc;
using AG2Router.AG2.Security;

namespace AG2Router.AG2.Usage;

/// <summary>
/// One validated, RPC-ready Antigravity language-server endpoint for usage collection. These
/// values are transient transport details only: they must never be persisted, logged, or
/// exposed through diagnostics.
/// </summary>
public sealed record UsageInstanceEndpoint(int ProcessId, int Port, string Protocol, string CsrfToken);

/// <summary>
/// Discovers every validated Antigravity language-server instance for usage collection.
/// Antigravity may run several language servers with disjoint conversation sets, so binding to
/// a single selected process would silently lose coverage; this discovery enumerates all
/// candidates that satisfy the repository's provenance rules (never loosened for usage),
/// deduplicates by process, resolves their loopback ports, and proves each port responds to an
/// authenticated Connect-RPC probe. Processes that vanish mid-enumeration are skipped.
/// </summary>
public sealed class UsageInstanceDiscovery
{
    private readonly IProcessInspector _inspector;
    private readonly IAG2RpcClient _probeClient;

    public UsageInstanceDiscovery(IProcessInspector inspector, IAG2RpcClient? probeClient = null)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _probeClient = probeClient ?? new AG2RpcClient();
    }

    /// <summary>All currently validated instances; empty when Antigravity is not running.</summary>
    public async Task<IReadOnlyList<UsageInstanceEndpoint>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var processes = await _inspector.FindProcessesAsync(cancellationToken).ConfigureAwait(false);
        var candidates = ProcessProvenanceValidator.ValidateAll(processes);
        if (candidates.Count == 0)
            return [];

        var endpoints = new List<UsageInstanceEndpoint>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<int> ports;
            try
            {
                ports = await _inspector.GetListeningPortsAsync(candidate.ProcessId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The process may have vanished between enumeration and port lookup; skip it
                // rather than failing the whole discovery.
                continue;
            }

            // Without a parseable CSRF token the endpoint cannot be authenticated; skip it.
            string csrfToken = AG2Security.ExtractCsrfToken(candidate.CommandLine) ?? string.Empty;
            if (csrfToken.Length == 0)
                continue;

            foreach (var port in ports)
            {
                if (await _probeClient.ProbePortAsync(port, "https", csrfToken, cancellationToken).ConfigureAwait(false))
                {
                    endpoints.Add(new UsageInstanceEndpoint(candidate.ProcessId, port, "https", csrfToken));
                    break;
                }
                if (await _probeClient.ProbePortAsync(port, "http", csrfToken, cancellationToken).ConfigureAwait(false))
                {
                    endpoints.Add(new UsageInstanceEndpoint(candidate.ProcessId, port, "http", csrfToken));
                    break;
                }
            }
        }

        return endpoints
            .GroupBy(static endpoint => (endpoint.ProcessId, endpoint.Port))
            .Select(static group => group.First())
            .OrderBy(static endpoint => endpoint.ProcessId)
            .ThenBy(static endpoint => endpoint.Port)
            .ToList();
    }
}
