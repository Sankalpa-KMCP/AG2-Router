using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using AG2Router.AG2.Security;

namespace AG2Router.App.Services;

/// <summary>
/// Coordinates background telemetry polling for the desktop application.
/// Strictly decoupled from WPF and WebView2 to preserve tray-first resource efficiency.
/// Guarantees:
/// 1. Sequential execution with zero overlapping polls.
/// 2. Stale response suppression via monotonic sequence numbering.
/// 3. Lockless atomic reads for Kestrel loopback server and UI views.
/// 4. Prompt cancellation during application shutdown.
/// 5. Coherent account identity and quota observation derived from a single upstream response.
/// </summary>
public class TelemetryPollingCoordinator : IAsyncDisposable
{
    private readonly IAG2Adapter _adapter;
    private TimeSpan _interval;
    private PeriodicTimer? _timer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _stateLock = new();

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private long _pollSequence;
    private long _lastAppliedSequence;
    private long _lastAppliedConfigGeneration;
    private SystemStatusDto _currentStatus;

    public event Action<SystemStatusDto>? StatusUpdated;

    private readonly INativeAutoRouter? _autoRouter;
    private readonly Action<string>? _log;

    public TelemetryPollingCoordinator(
        IAG2Adapter adapter,
        TimeSpan? interval = null,
        INativeAutoRouter? autoRouter = null,
        Action<string>? log = null)
    {
        _adapter = adapter;
        if (interval.HasValue && interval.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Polling interval must be positive.");
        }
        _interval = interval ?? TimeSpan.FromSeconds(10);
        _autoRouter = autoRouter;
        _log = log;
        if (_autoRouter != null)
        {
            _lastAppliedConfigGeneration = _autoRouter.ConfigGeneration;
        }

        _currentStatus = new SystemStatusDto(
            Status: "ok",
            Ag2: new Ag2StatusDto(
                Connected: false,
                Status: "INITIALIZING",
                Activity: new ActivityStatusDto("INITIALIZING", 0, 0, DateTime.UtcNow.ToString("o")),
                Message: "Initializing telemetry coordinator..."
            ),
            Router: _autoRouter?.GetStatus() ?? new RouterStatusDto(
                State: "IDLE",
                AutoSwitchEnabled: false,
                ActiveAccountId: null,
                ActiveAccountEmail: null,
                PendingTargetAccountId: null,
                LastEvaluatedAt: null,
                LastDecisionReason: "Starting telemetry coordinator...",
                Config: new RouterConfigDto()
            ),
            Telemetry: null
        );
    }

    public TimeSpan Interval
    {
        get
        {
            lock (_stateLock)
            {
                return _interval;
            }
        }
    }

    public long LastAppliedConfigGeneration
    {
        get
        {
            lock (_stateLock)
            {
                return _lastAppliedConfigGeneration;
            }
        }
    }

    /// <summary>
    /// Updates the polling interval with monotonic configuration generation tracking (R05).
    /// </summary>
    /// <param name="newInterval">The new periodic polling interval.</param>
    /// <param name="generation">The configuration generation associated with this interval update (0 for generation-less calls).</param>
    /// <returns><c>true</c> if the interval was applied; <c>false</c> if rejected as stale or invalid.</returns>
    /// <remarks>
    /// <para>
    /// <b>Stale Update Suppression:</b>
    /// When <paramref name="generation"/> &gt; 0, any update with a generation strictly less than
    /// <see cref="_lastAppliedConfigGeneration"/> is rejected. This prevents race conditions where
    /// an older slow configuration write overwrites a newer configuration update's polling interval.
    /// </para>
    /// </remarks>
    public bool UpdateInterval(TimeSpan newInterval, long generation = 0)
    {
        lock (_stateLock)
        {
            if (newInterval <= TimeSpan.Zero) return false;
            if (generation > 0)
            {
                if (generation < _lastAppliedConfigGeneration)
                {
                    _log?.Invoke($"Rejected stale polling interval update: generation {generation} is older than last applied generation {_lastAppliedConfigGeneration}.");
                    return false;
                }
                _lastAppliedConfigGeneration = generation;
            }

            _interval = newInterval;
            if (_timer != null)
            {
                try
                {
                    _timer.Period = newInterval;
                }
                catch { }
            }
            return true;
        }
    }

    public void UpdateInterval(TimeSpan newInterval) => UpdateInterval(newInterval, 0);

    public SystemStatusDto CurrentStatus => Volatile.Read(ref _currentStatus);

    public void Start()
    {
        if (_cts != null) return; // Already started

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Start background polling loop: immediate initial poll followed by periodic timer ticks
        _pollTask = Task.Run(async () =>
        {
            try
            {
                await PollAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Background telemetry polling loop encountered an unexpected error: {AG2Security.RedactSensitiveText(ex.GetType().Name + ": " + ex.Message)}");
            }

            PeriodicTimer timer;
            lock (_stateLock)
            {
                if (_interval <= TimeSpan.Zero)
                {
                    _log?.Invoke("Polling interval must be positive; cannot start timer.");
                    return;
                }
                _timer = new PeriodicTimer(_interval);
                timer = _timer;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                        {
                            break;
                        }
                        await PollAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"Background telemetry polling loop encountered an unexpected error: {AG2Security.RedactSensitiveText(ex.GetType().Name + ": " + ex.Message)}");
                    }
                }
            }
            finally
            {
                lock (_stateLock)
                {
                    timer.Dispose();
                    if (ReferenceEquals(_timer, timer))
                    {
                        _timer = null;
                    }
                }
            }
        }, token);
    }

    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Guard against overlapping polls: if a poll is already active, skip this tick cleanly
        try
        {
            if (!await _semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        long sequence = Interlocked.Increment(ref _pollSequence);

        try
        {
            var ag2Status = await _adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);

            if (_autoRouter != null)
            {
                try
                {
                    await _autoRouter.EvaluateCycleAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Clean cancellation during shutdown; don't log as an error
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"AutoRouter evaluation cycle failed: {AG2Security.RedactSensitiveText(ex.GetType().Name + ": " + ex.Message)}");
                }
            }

            var routerDto = _autoRouter?.GetStatus();

            SystemStatusDto newStatus;
            if (ag2Status.Connected)
            {
                var observation = await _adapter.GetAccountQuotaObservationAsync(cancellationToken).ConfigureAwait(false);
                var account = observation?.Account;
                var quota = observation?.Quota;

                // Check for stale response before assembling snapshot
                if (sequence < Volatile.Read(ref _lastAppliedSequence))
                {
                    return; // Discard stale response
                }

                var activity = await _adapter.GetActivityStateAsync(cancellationToken).ConfigureAwait(false);

                newStatus = new SystemStatusDto(
                    Status: "ok",
                    Ag2: new Ag2StatusDto(
                        Connected: true,
                        Status: ag2Status.Status,
                        Activity: activity,
                        Message: ag2Status.Message
                    ),
                    Router: routerDto ?? new RouterStatusDto(
                        State: "HEALTHY",
                        AutoSwitchEnabled: false,
                        ActiveAccountId: account?.Email,
                        ActiveAccountEmail: account?.Email,
                        PendingTargetAccountId: null,
                        LastEvaluatedAt: DateTime.UtcNow.ToString("o"),
                        LastDecisionReason: $"Account {account?.Email ?? "none"} active with {quota?.Models?.Count ?? 0} models.",
                        Config: new RouterConfigDto()
                    ),
                    Telemetry: new TelemetryDto(
                        CurrentAccount: account,
                        Quota: quota,
                        Activity: activity,
                        TotalAvailableQuotaPercent: null,
                        LastSuccessfulTelemetry: DateTime.UtcNow.ToString("o")
                    )
                );
            }
            else
            {
                newStatus = new SystemStatusDto(
                    Status: "ok",
                    Ag2: ag2Status,
                    Router: routerDto ?? new RouterStatusDto(
                        State: "DEGRADED",
                        AutoSwitchEnabled: false,
                        ActiveAccountId: null,
                        ActiveAccountEmail: null,
                        PendingTargetAccountId: null,
                        LastEvaluatedAt: DateTime.UtcNow.ToString("o"),
                        LastDecisionReason: ag2Status.Message ?? "Antigravity offline or not detected",
                        Config: new RouterConfigDto()
                    ),
                    Telemetry: null
                );
            }

            if (TryApplyStatus(sequence, newStatus))
            {
                try
                {
                    StatusUpdated?.Invoke(newStatus);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"StatusUpdated subscriber threw an exception: {AG2Security.RedactSensitiveText(ex.GetType().Name + ": " + ex.Message)}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean cancellation during shutdown
        }
        catch (Exception ex)
        {
            var safeMessage = AG2Security.RedactSensitiveText(ex.Message);
            _log?.Invoke($"Telemetry polling failed: {AG2Security.RedactSensitiveText(ex.GetType().Name + ": " + safeMessage)}");
            var fallbackStatus = new SystemStatusDto(
                Status: "error",
                Ag2: new Ag2StatusDto(
                    Connected: false,
                    Status: "ERROR",
                    Activity: new ActivityStatusDto("ERROR", 0, 0, DateTime.UtcNow.ToString("o")),
                    Message: $"Polling error: {safeMessage}"
                ),
                Router: new RouterStatusDto(
                    State: "ERROR",
                    AutoSwitchEnabled: false,
                    ActiveAccountId: null,
                    ActiveAccountEmail: null,
                    PendingTargetAccountId: null,
                    LastEvaluatedAt: DateTime.UtcNow.ToString("o"),
                    LastDecisionReason: $"Polling error: {safeMessage}",
                    Config: new RouterConfigDto()
                ),
                Telemetry: null
            );

            if (TryApplyStatus(sequence, fallbackStatus))
            {
                try
                {
                    StatusUpdated?.Invoke(fallbackStatus);
                }
                catch (Exception subscriberEx)
                {
                    _log?.Invoke($"StatusUpdated subscriber threw an exception on fallback status: {AG2Security.RedactSensitiveText(subscriberEx.GetType().Name + ": " + subscriberEx.Message)}");
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    internal bool TryApplyStatus(long sequence, SystemStatusDto status)
    {
        lock (_stateLock)
        {
            if (sequence >= _lastAppliedSequence)
            {
                _lastAppliedSequence = sequence;
                Volatile.Write(ref _currentStatus, status);
                return true;
            }
            return false; // Discard superseded status
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            if (_pollTask != null)
            {
                try
                {
                    await _pollTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch { }
            }

            _cts.Dispose();
            _cts = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _semaphore.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { }
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
