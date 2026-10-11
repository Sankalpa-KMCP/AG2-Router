using AG2Router.AG2.Discovery;
using AG2Router.AG2.Rpc;
using AG2Router.AG2.Security;
using AG2Router.Core.Diagnostics;

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
    private readonly IDiagnosticSink _diagnosticSink;

    public UsageInstanceDiscovery(
        IProcessInspector inspector,
        IAG2RpcClient? probeClient = null,
        IDiagnosticSink? diagnosticSink = null)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _probeClient = probeClient ?? new AG2RpcClient();
        _diagnosticSink = diagnosticSink ?? NullDiagnosticSink.Instance;
    }

    /// <summary>All currently validated instances; empty when Antigravity is not running.</summary>
    public async Task<IReadOnlyList<UsageInstanceEndpoint>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var processes = await _inspector.FindProcessesAsync(cancellationToken).ConfigureAwait(false);
        var candidates = ProcessProvenanceValidator.ValidateAll(processes);
        if (candidates.Count == 0)
            return [];

        var endpoints = new List<UsageInstanceEndpoint>();
        string discoveryCorrelationId = DiagnosticRunContext.CreateCorrelationId();
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
                var httpsResult = await _probeClient.ProbePortDetailedAsync(port, "https", csrfToken, cancellationToken).ConfigureAwait(false);
                if (httpsResult.Success)
                {
                    endpoints.Add(new UsageInstanceEndpoint(candidate.ProcessId, port, "https", csrfToken));
                    break;
                }
                else
                {
                    EmitProbeDiagnostic(httpsResult.FailureReason, "https", httpsResult.StatusCode, discoveryCorrelationId);
                }

                var httpResult = await _probeClient.ProbePortDetailedAsync(port, "http", csrfToken, cancellationToken).ConfigureAwait(false);
                if (httpResult.Success)
                {
                    endpoints.Add(new UsageInstanceEndpoint(candidate.ProcessId, port, "http", csrfToken));
                    break;
                }
                else
                {
                    EmitProbeDiagnostic(httpResult.FailureReason, "http", httpResult.StatusCode, discoveryCorrelationId);
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

    private void EmitProbeDiagnostic(ProbeFailureReason reason, string protocol, int statusCode, string? correlationId = null)
    {
        if (reason == ProbeFailureReason.None) return;

        int eventId = reason switch
        {
            ProbeFailureReason.ProtocolMismatch => DiagnosticEventCatalog.RpcProtocolMismatch,
            ProbeFailureReason.TlsHandshakeFailure => DiagnosticEventCatalog.TlsHandshakeFailed,
            ProbeFailureReason.ProbeTimeout => DiagnosticEventCatalog.RpcDeadlineExceeded,
            ProbeFailureReason.Cancelled => DiagnosticEventCatalog.RpcCallerCancelled,
            ProbeFailureReason.IncompleteResponseBody => DiagnosticEventCatalog.RpcBodyIncomplete,
            ProbeFailureReason.RejectedRedirect => DiagnosticEventCatalog.RpcRejectedRedirect,
            ProbeFailureReason.ServerError => DiagnosticEventCatalog.RpcServerError,
            ProbeFailureReason.TransportError => DiagnosticEventCatalog.RpcTransportFailed,
            _ => DiagnosticEventCatalog.RpcUnknownFailure
        };

        DiagnosticFailureCategory category = reason switch
        {
            ProbeFailureReason.ProtocolMismatch => DiagnosticFailureCategory.ProtocolMismatch,
            ProbeFailureReason.TlsHandshakeFailure => DiagnosticFailureCategory.TlsHandshakeFailure,
            ProbeFailureReason.ProbeTimeout => DiagnosticFailureCategory.ProbeTimeout,
            ProbeFailureReason.Cancelled => DiagnosticFailureCategory.Cancelled,
            ProbeFailureReason.IncompleteResponseBody => DiagnosticFailureCategory.IncompleteResponseBody,
            ProbeFailureReason.RejectedRedirect => DiagnosticFailureCategory.RejectedRedirect,
            ProbeFailureReason.ServerError => DiagnosticFailureCategory.ServerError,
            ProbeFailureReason.TransportError => DiagnosticFailureCategory.TransportError,
            _ => DiagnosticFailureCategory.Unknown
        };

        var evt = DiagnosticEventFactory.Create(
            eventId: eventId,
            component: DiagnosticComponents.UsageDiscovery,
            operation: DiagnosticOperations.ProbeConnectRpcPort,
            outcome: reason == ProbeFailureReason.Cancelled ? DiagnosticOutcome.Cancelled : DiagnosticOutcome.Failure,
            failureCategory: category,
            correlationId: correlationId,
            safeMessage: $"Usage instance port probe failed for {protocol.ToUpperInvariant()} with reason {reason}.",
            protocol: protocol,
            httpStatus: statusCode > 0 ? statusCode : null,
            severity: reason == ProbeFailureReason.Cancelled ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning,
            affectedFeature: AffectedFeature.RpcTransport
        );

        _diagnosticSink.Emit(evt);
    }
}
