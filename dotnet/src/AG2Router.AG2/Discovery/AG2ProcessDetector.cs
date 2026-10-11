using System.Diagnostics;
using AG2Router.AG2.Rpc;
using AG2Router.AG2.Security;
using AG2Router.Core.Diagnostics;

namespace AG2Router.AG2.Discovery;

/// <summary>
/// Discovers and tracks the live Antigravity 2 language server daemon.
/// Implements session caching, offline scan cooldown, circuit breaker on repeated RPC failures,
/// and sequential port probing.
/// </summary>
public class AG2ProcessDetector
{
    private readonly IProcessInspector _inspector;
    private readonly IAG2RpcClient _rpcClient;
    private readonly IDiagnosticSink _diagnosticSink;
    private readonly TimeSpan _offlineCooldown;
    private readonly int _failureThreshold;

    private readonly object _lock = new();
    private CachedAG2Session? _cachedSession;
    private long _lastOfflineScanTimestamp;
    private int _consecutiveFailures;
    private long _sessionGeneration;
    private CancellationTokenSource? _sessionCts;
    private bool _discoveryDegraded;
    private string? _degradedCorrelationId;

    public AG2ProcessDetector(
        IProcessInspector? inspector = null,
        IAG2RpcClient? rpcClient = null,
        TimeSpan? offlineCooldown = null,
        int failureThreshold = 3,
        IDiagnosticSink? diagnosticSink = null)
    {
        _inspector = inspector ?? new WindowsProcessInspector();
        _rpcClient = rpcClient ?? new AG2RpcClient();
        _offlineCooldown = offlineCooldown ?? TimeSpan.FromSeconds(15);
        _failureThreshold = failureThreshold;
        _diagnosticSink = diagnosticSink ?? NullDiagnosticSink.Instance;
    }

    public long CurrentGeneration
    {
        get { lock (_lock) { return _sessionGeneration; } }
    }

    public CachedAG2Session? GetCachedSession()
    {
        lock (_lock)
        {
            if (_cachedSession == null) return null;

            if (!_inspector.IsPidAlive(_cachedSession.Pid, _cachedSession.StartTime))
            {
                InvalidateCacheInternal("Cached PID is no longer alive");
                return null;
            }

            return _cachedSession;
        }
    }

    public void InvalidateCache(string? reason = null)
    {
        lock (_lock)
        {
            InvalidateCacheInternal(reason);
        }
    }

    /// <summary>
    /// Clears cached generation and offline throttling before an explicitly authorized
    /// process transition. This is never called by telemetry polling or startup.
    /// </summary>
    public void PrepareForProcessTransition()
    {
        lock (_lock)
        {
            InvalidateCacheInternal("Explicit process transition");
            _lastOfflineScanTimestamp = 0;
            _consecutiveFailures = 0;
        }
    }

    private void InvalidateCacheInternal(string? reason)
    {
        if (_cachedSession != null)
        {
            _cachedSession = null;
            _sessionGeneration++;
            try
            {
                _sessionCts?.Cancel();
                _sessionCts?.Dispose();
            }
            catch { }
            _sessionCts = null;
        }
    }

    public void RecordRpcSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
        }
    }

    public void RecordRpcFailure(Exception? ex = null)
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                InvalidateCacheInternal($"Exceeded {_failureThreshold} consecutive RPC failures");
                _consecutiveFailures = 0;
            }
        }
    }

    public async Task<AG2DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        // 1. Fast path: check cached session
        var cached = GetCachedSession();
        if (cached != null)
        {
            var publicInfo = new AG2ProcessInfo(
                cached.Pid,
                cached.Port,
                cached.Protocol,
                AG2Security.MaskToken(cached.CsrfToken),
                cached.SanitizedCommandLine,
                cached.BinaryPath,
                cached.DiscoveredAt
            );

            return new AG2DiscoveryResult(
                IsRunning: true,
                Status: "HEALTHY",
                ProcessInfo: publicInfo,
                Message: $"Antigravity 2 connected (PID {cached.Pid}, port {cached.Port} via {cached.Protocol.ToUpperInvariant()})"
            );
        }

        // 2. Offline cooldown check
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_lastOfflineScanTimestamp > 0)
            {
                var elapsed = Stopwatch.GetElapsedTime(_lastOfflineScanTimestamp, now);
                if (elapsed < _offlineCooldown)
                {
                    return new AG2DiscoveryResult(
                        IsRunning: false,
                        Status: "OFFLINE",
                        ProcessInfo: null,
                        Message: "Antigravity 2 is offline (cooldown active)"
                    );
                }
            }
        }

        // 3. Cold discovery: enumerate candidate processes
        IReadOnlyList<DiscoveredProcessRaw> processes;
        try
        {
            processes = await _inspector.FindProcessesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_lock) { _lastOfflineScanTimestamp = Stopwatch.GetTimestamp(); }
            return new AG2DiscoveryResult(
                IsRunning: false,
                Status: "OFFLINE",
                ProcessInfo: null,
                Message: $"Process inspection error: {ex.Message}"
            );
        }

        // 4. Validate provenance & disambiguate candidates
        var validation = ProcessProvenanceValidator.Validate(processes);
        if (validation.Status == ProvenanceStatus.NoCandidates)
        {
            lock (_lock) { _lastOfflineScanTimestamp = Stopwatch.GetTimestamp(); }
            return new AG2DiscoveryResult(
                IsRunning: false,
                Status: "OFFLINE",
                ProcessInfo: null,
                Message: validation.Message
            );
        }

        if (validation.Status != ProvenanceStatus.Discovered || validation.SelectedProcess == null || validation.CsrfToken == null)
        {
            // Process found, but degraded (unrelated, ambiguous, or missing token)
            return new AG2DiscoveryResult(
                IsRunning: true,
                Status: "DEGRADED",
                ProcessInfo: null,
                Message: validation.Message
            );
        }

        // Process found; reset offline scan timer
        lock (_lock) { _lastOfflineScanTimestamp = 0; }

        var targetProc = validation.SelectedProcess;
        var csrfToken = validation.CsrfToken;

        // 5. Query listening ports
        IReadOnlyList<int> listeningPorts;
        try
        {
            listeningPorts = await _inspector.GetListeningPortsAsync(targetProc.ProcessId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new AG2DiscoveryResult(
                IsRunning: true,
                Status: "DEGRADED",
                ProcessInfo: null,
                Message: $"Failed to query ports for PID {targetProc.ProcessId}: {ex.Message}"
            );
        }

        if (listeningPorts.Count == 0)
        {
            return new AG2DiscoveryResult(
                IsRunning: true,
                Status: "DEGRADED",
                ProcessInfo: null,
                Message: $"Antigravity 2 (PID {targetProc.ProcessId}) has no active loopback listening ports"
            );
        }

        // 6. Probe candidate ports (HTTPS first, then HTTP)
        int workingPort = -1;
        string workingProtocol = "https";
        string passCorrelationId = DiagnosticRunContext.CreateCorrelationId();

        foreach (var port in listeningPorts)
        {
            var httpsResult = await _rpcClient.ProbePortDetailedAsync(port, "https", csrfToken, cancellationToken).ConfigureAwait(false);
            if (httpsResult.Success)
            {
                workingPort = port;
                workingProtocol = "https";
                break;
            }
            else
            {
                EmitProbeDiagnostic(httpsResult.FailureReason, "https", httpsResult.StatusCode, passCorrelationId);
            }

            var httpResult = await _rpcClient.ProbePortDetailedAsync(port, "http", csrfToken, cancellationToken).ConfigureAwait(false);
            if (httpResult.Success)
            {
                workingPort = port;
                workingProtocol = "http";
                break;
            }
            else
            {
                EmitProbeDiagnostic(httpResult.FailureReason, "http", httpResult.StatusCode, passCorrelationId);
            }
        }

        if (workingPort == -1)
        {
            _discoveryDegraded = true;
            _degradedCorrelationId = passCorrelationId;
            _diagnosticSink.Emit(DiagnosticEventFactory.Create(
                eventId: DiagnosticEventCatalog.DiscoveryFailed,
                component: DiagnosticComponents.Discovery,
                operation: DiagnosticOperations.DiscoverProcess,
                outcome: DiagnosticOutcome.Failure,
                failureCategory: DiagnosticFailureCategory.DiscoveryError,
                correlationId: passCorrelationId,
                safeMessage: "Candidate ports failed Connect-RPC probe.",
                severity: DiagnosticSeverity.Warning,
                affectedFeature: AffectedFeature.Discovery
            ));

            return new AG2DiscoveryResult(
                IsRunning: true,
                Status: "DEGRADED",
                ProcessInfo: null,
                Message: $"Antigravity 2 (PID {targetProc.ProcessId}) ports [{string.Join(", ", listeningPorts)}] did not respond to Connect-RPC probe"
            );
        }

        if (_discoveryDegraded)
        {
            _discoveryDegraded = false;
            string recoveryCorrelationId = _degradedCorrelationId ?? passCorrelationId;
            _degradedCorrelationId = null;
            _diagnosticSink.Emit(DiagnosticEventFactory.Create(
                eventId: DiagnosticEventCatalog.DiscoveryRecovered,
                component: DiagnosticComponents.Discovery,
                operation: DiagnosticOperations.DiscoverProcess,
                outcome: DiagnosticOutcome.Success,
                failureCategory: DiagnosticFailureCategory.None,
                correlationId: recoveryCorrelationId,
                safeMessage: $"Antigravity 2 discovery recovered via {workingProtocol.ToUpperInvariant()}.",
                recoveryStatus: RecoveryStatus.Succeeded,
                protocol: workingProtocol,
                severity: DiagnosticSeverity.Info,
                affectedFeature: AffectedFeature.Discovery
            ));
        }

        // 7. Establish new cached session
        var discoveredAt = DateTime.UtcNow.ToString("o");
        var sanitizedCmd = AG2Security.SanitizeCommandLine(targetProc.CommandLine);

        lock (_lock)
        {
            _sessionGeneration++;
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = new CancellationTokenSource();

            _cachedSession = new CachedAG2Session(
                Pid: targetProc.ProcessId,
                Port: workingPort,
                Protocol: workingProtocol,
                CsrfToken: csrfToken,
                BinaryPath: targetProc.ExecutablePath,
                SanitizedCommandLine: sanitizedCmd,
                DiscoveredAt: discoveredAt,
                StartTime: targetProc.StartTime,
                Generation: _sessionGeneration
            );
            _consecutiveFailures = 0;
        }

        var publicProcessInfo = new AG2ProcessInfo(
            targetProc.ProcessId,
            workingPort,
            workingProtocol,
            AG2Security.MaskToken(csrfToken),
            sanitizedCmd,
            targetProc.ExecutablePath,
            discoveredAt
        );

        return new AG2DiscoveryResult(
            IsRunning: true,
            Status: "DISCOVERED",
            ProcessInfo: publicProcessInfo,
            Message: $"Discovered Antigravity 2 (PID {targetProc.ProcessId}, port {workingPort} via {workingProtocol.ToUpperInvariant()})"
        );
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
            component: DiagnosticComponents.Discovery,
            operation: DiagnosticOperations.ProbeConnectRpcPort,
            outcome: reason == ProbeFailureReason.Cancelled ? DiagnosticOutcome.Cancelled : DiagnosticOutcome.Failure,
            failureCategory: category,
            correlationId: correlationId,
            safeMessage: $"Port probe failed for {protocol.ToUpperInvariant()} with reason {reason}.",
            protocol: protocol,
            httpStatus: statusCode > 0 ? statusCode : null,
            severity: reason == ProbeFailureReason.Cancelled ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning,
            affectedFeature: AffectedFeature.RpcTransport
        );

        _diagnosticSink.Emit(evt);
    }
}
