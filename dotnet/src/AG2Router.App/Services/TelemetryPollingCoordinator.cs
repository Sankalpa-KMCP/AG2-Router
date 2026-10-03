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
    private readonly CancellationTokenSource _lifetimeCts = new();
    private TaskCompletionSource? _activePollCompletion;
    private CancellationTokenSource? _activePollCts;
    private Task? _disposeDrainTask;
    private bool _stopping;
    private bool _disposeRequested;
    internal Task DisposalCompletion { get { lock (_stateLock) return _disposeDrainTask ?? Task.CompletedTask; } }
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
            if (_disposeRequested || newInterval <= TimeSpan.Zero) return false;
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
        lock (_stateLock)
        {
            if (_disposeRequested || (_pollTask != null && !_pollTask.IsCompleted) ||
                (_cts != null && _activePollCompletion != null)) return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _stopping = false;
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
                    if (_stopping || _disposeRequested || token.IsCancellationRequested) return;
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
            // Observe even unexpected loop faults (for example a failing diagnostic subscriber).
            _ = _pollTask.ContinueWith(task =>
                System.Diagnostics.Trace.TraceError(AG2Security.RedactSensitiveText(task.Exception!.GetBaseException().GetType().Name + ": " + task.Exception.GetBaseException().Message)),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource pollCts;
        TaskCompletionSource completion;
        lock (_stateLock)
        {
            if (_stopping || _disposeRequested || cancellationToken.IsCancellationRequested) return;
            // Admission and disposal share one owner. The zero-wait gate never queues polls.
            if (!_semaphore.Wait(0)) return;
            pollCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCts.Token, _cts?.Token ?? CancellationToken.None);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activePollCompletion = completion;
            _activePollCts = pollCts;
        }
        cancellationToken = pollCts.Token;

        long sequence = Interlocked.Increment(ref _pollSequence);

        try
        {
            var ag2Status = await _adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (_autoRouter != null)
            {
                try
                {
                    await _autoRouter.EvaluateCycleAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw; // Stop snapshot assembly on expected shutdown cancellation.
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"AutoRouter evaluation cycle failed: {AG2Security.RedactSensitiveText(ex.GetType().Name + ": " + ex.Message)}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var routerDto = _autoRouter?.GetStatus();

            SystemStatusDto newStatus;
            if (ag2Status.Connected)
            {
                var observation = await _adapter.GetAccountQuotaObservationAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var account = observation?.Account;
                var quota = observation?.Quota;

                // Check for stale response before assembling snapshot
                if (sequence < Volatile.Read(ref _lastAppliedSequence))
                {
                    return; // Discard stale response
                }

                var activity = await _adapter.GetActivityStateAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

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

            PublishStatus(sequence, newStatus);
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
                Router: _autoRouter?.GetStatus() ?? new RouterStatusDto(
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

            PublishStatus(sequence, fallbackStatus, fallback: true);
        }
        finally
        {
            lock (_stateLock)
            {
                pollCts.Dispose();
                _semaphore.Release();
                _activePollCompletion = null;
                _activePollCts = null;
                completion.TrySetResult();
            }
        }
    }

    private void PublishStatus(long sequence, SystemStatusDto status, bool fallback = false)
    {
        lock (_stateLock)
        {
            if (!TryApplyStatus(sequence, status)) return;
            try { StatusUpdated?.Invoke(status); }
            catch (Exception ex)
            {
                string context = fallback ? " on fallback status" : string.Empty;
                _log?.Invoke($"StatusUpdated subscriber threw an exception{context}: {AG2Security.RedactSensitiveText(ex.GetType().Name + ": " + ex.Message)}");
            }
        }
    }

    internal bool TryApplyStatus(long sequence, SystemStatusDto status)
    {
        lock (_stateLock)
        {
            if (!_stopping && !_disposeRequested && sequence >= _lastAppliedSequence)
            {
                _lastAppliedSequence = sequence;
                Volatile.Write(ref _currentStatus, status);
                return true;
            }
            return false; // Discard superseded status
        }
    }

    /// <summary>Requests stop and drains admitted work within the caller's wait budget.
    /// A canceled wait retains task/CTS ownership; a late poll cannot publish after stop.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task background;
        Task active;
        lock (_stateLock)
        {
            if (_disposeRequested)
            {
                background = _disposeDrainTask ?? Task.CompletedTask;
                active = Task.CompletedTask;
            }
            else
            {
                _stopping = true;
                _cts?.Cancel();
                _activePollCts?.Cancel();
                _timer?.Dispose();
                background = _pollTask ?? Task.CompletedTask;
                active = _activePollCompletion?.Task ?? Task.CompletedTask;
            }
        }
        try
        {
            await background.WaitAsync(cancellationToken).ConfigureAwait(false);
            await active.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || background.IsCanceled) { }
    }

    /// <summary>Closes admission/publication immediately and waits at most two seconds.
    /// Cancellation-ignoring work retains its resources until the owned drain completes.</summary>
    public async ValueTask DisposeAsync()
    {
        Task drain;
        lock (_stateLock)
        {
            if (_disposeDrainTask == null)
            {
                _disposeRequested = true;
                _stopping = true;
                _lifetimeCts.Cancel();
                _cts?.Cancel();
                _timer?.Dispose();
                var background = _pollTask ?? Task.CompletedTask;
                var active = _activePollCompletion?.Task ?? Task.CompletedTask;
                _disposeDrainTask = Task.Run(async () =>
                {
                    try { await background.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (background.IsCanceled) { }
                    finally
                    {
                        await active.ConfigureAwait(false);
                        lock (_stateLock)
                        {
                            _timer?.Dispose();
                            _timer = null;
                            _cts?.Dispose();
                            _cts = null;
                            _lifetimeCts.Dispose();
                            _semaphore.Dispose();
                        }
                    }
                });
                _ = _disposeDrainTask.ContinueWith(task =>
                    System.Diagnostics.Trace.TraceError(AG2Security.RedactSensitiveText(task.Exception!.GetBaseException().GetType().Name + ": " + task.Exception.GetBaseException().Message)),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            drain = _disposeDrainTask;
        }
        try { await drain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) when (!drain.IsFaulted) { /* The owned drain retains resources and observes late completion. */ }
        GC.SuppressFinalize(this);
    }
}
