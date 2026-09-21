using AG2Router.AG2.Discovery;
using AG2Router.AG2.Normalization;
using AG2Router.AG2.Rpc;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Adapter;

/// <summary>
/// Live implementation of IAG2Adapter providing read-only process discovery,
/// Connect-RPC telemetry retrieval, and data normalization.
/// </summary>
public class AG2LiveAdapter : IAG2Adapter
{
    private readonly AG2ProcessDetector _detector;
    private readonly IAG2RpcClient _rpcClient;
    private string? _lastTelemetryTimestamp;

    public AG2LiveAdapter(AG2ProcessDetector? detector = null, IAG2RpcClient? rpcClient = null)
    {
        _detector = detector ?? new AG2ProcessDetector();
        _rpcClient = rpcClient ?? new AG2RpcClient();
    }

    public AG2ProcessDetector Detector => _detector;
    public string? LastTelemetryTimestamp => _lastTelemetryTimestamp;

    public async Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var discovery = await _detector.DiscoverAsync(cancellationToken).ConfigureAwait(false);

        if (!discovery.IsRunning || discovery.ProcessInfo == null)
        {
            return new Ag2StatusDto(
                Connected: false,
                Status: discovery.Status,
                Activity: new ActivityStatusDto(discovery.Status, 0, 0, DateTime.UtcNow.ToString("o")),
                Message: discovery.Message
            );
        }

        var activity = await GetActivityStateAsync(cancellationToken).ConfigureAwait(false);

        return new Ag2StatusDto(
            Connected: true,
            Status: discovery.Status,
            Activity: activity,
            Message: discovery.Message
        );
    }

    public async Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session == null)
        {
            return null;
        }

        try
        {
            var raw = await _rpcClient.GetUserStatusAsync(
                session.Port,
                session.Protocol,
                session.CsrfToken,
                cancellationToken).ConfigureAwait(false);

            _detector.RecordRpcSuccess();
            _lastTelemetryTimestamp = DateTime.UtcNow.ToString("o");
            return AG2TelemetryNormalizer.NormalizeAccountIdentity(raw);
        }
        catch (Exception ex)
        {
            _detector.RecordRpcFailure(ex);
            return null;
        }
    }

    public async Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session == null)
        {
            return null;
        }

        try
        {
            var raw = await _rpcClient.GetUserStatusAsync(
                session.Port,
                session.Protocol,
                session.CsrfToken,
                cancellationToken).ConfigureAwait(false);

            _detector.RecordRpcSuccess();
            _lastTelemetryTimestamp = DateTime.UtcNow.ToString("o");
            return AG2TelemetryNormalizer.NormalizeQuotaSnapshot(raw);
        }
        catch (Exception ex)
        {
            _detector.RecordRpcFailure(ex);
            return null;
        }
    }

    public async Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default)
    {
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session == null)
        {
            return new ActivityStatusDto(
                State: "OFFLINE",
                TotalTrajectories: 0,
                RunningTrajectories: 0,
                Timestamp: DateTime.UtcNow.ToString("o")
            );
        }

        try
        {
            var raw = await _rpcClient.GetAllCascadeTrajectoriesAsync(
                session.Port,
                session.Protocol,
                session.CsrfToken,
                cancellationToken).ConfigureAwait(false);

            _detector.RecordRpcSuccess();
            _lastTelemetryTimestamp = DateTime.UtcNow.ToString("o");
            return AG2TelemetryNormalizer.NormalizeActivitySnapshot(raw);
        }
        catch (Exception ex)
        {
            _detector.RecordRpcFailure(ex);
            return new ActivityStatusDto(
                State: "ERROR",
                TotalTrajectories: 0,
                RunningTrajectories: 0,
                Timestamp: DateTime.UtcNow.ToString("o")
            );
        }
    }

    private async Task<CachedAG2Session?> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        var session = _detector.GetCachedSession();
        if (session != null)
        {
            return session;
        }

        var discovery = await _detector.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (!discovery.IsRunning || discovery.ProcessInfo == null)
        {
            return null;
        }

        return _detector.GetCachedSession();
    }
}
