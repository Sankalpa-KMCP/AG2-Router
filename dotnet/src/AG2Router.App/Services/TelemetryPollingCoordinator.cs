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
    private SystemStatusDto _currentStatus;

    public event Action<SystemStatusDto>? StatusUpdated;

    private readonly INativeAutoRouter? _autoRouter;

    public TelemetryPollingCoordinator(
        IAG2Adapter adapter,
        TimeSpan? interval = null,
        INativeAutoRouter? autoRouter = null)
    {
        _adapter = adapter;
        _interval = interval ?? TimeSpan.FromSeconds(10);
        _autoRouter = autoRouter;

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

    public void UpdateInterval(TimeSpan newInterval)
    {
        if (newInterval <= TimeSpan.Zero) return;
        lock (_stateLock)
        {
            _interval = newInterval;
            if (_timer != null)
            {
                try
                {
                    _timer.Period = newInterval;
                }
                catch { }
            }
        }
    }

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
            catch
            {
                // Swallowed to prevent crashing background thread
            }

            PeriodicTimer timer;
            lock (_stateLock)
            {
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
                    catch
                    {
                        // Swallowed to prevent crashing background thread
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
        // Guard against overlapping polls: if a poll is already active, skip this tick cleanly
        if (!await _semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
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
                catch
                {
                    // Swallowed to prevent telemetry loop abort
                }
            }

            var routerDto = _autoRouter?.GetStatus();

            SystemStatusDto newStatus;
            if (ag2Status.Connected)
            {
                var account = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
                var quota = await _adapter.GetQuotaAsync(cancellationToken).ConfigureAwait(false);

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
                catch { }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean cancellation during shutdown
        }
        catch (Exception ex)
        {
            var safeMessage = AG2Security.RedactSensitiveText(ex.Message);
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
                catch { }
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
