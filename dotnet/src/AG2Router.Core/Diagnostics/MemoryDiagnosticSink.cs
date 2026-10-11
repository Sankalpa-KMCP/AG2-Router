namespace AG2Router.Core.Diagnostics;

/// <summary>
/// Safe in-memory diagnostic sink used for pre-admission startup logging (R11) and test environments.
/// Enforces bounded memory limits without acquiring file locks or modifying disk logs.
/// </summary>
public sealed class MemoryDiagnosticSink : IDiagnosticSink
{
    public const int DefaultCapacity = 256;
    public const long DefaultMemoryLimitBytes = 2 * 1024 * 1024; // 2 MiB

    private readonly object _syncLock = new();
    private readonly List<DiagnosticEvent> _events = new();
    private readonly int _capacity;
    private readonly long _maxMemoryBytes;
    private long _currentMemoryBytes;
    private long _totalEmitted;

    public MemoryDiagnosticSink(int capacity = DefaultCapacity, long maxMemoryBytes = DefaultMemoryLimitBytes)
    {
        _capacity = Math.Max(1, capacity);
        _maxMemoryBytes = Math.Max(128, maxMemoryBytes);
    }

    public void Emit(DiagnosticEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var sanitized = @event.Sanitize();
        Interlocked.Increment(ref _totalEmitted);

        lock (_syncLock)
        {
            _events.Add(sanitized);
            // Estimate event memory
            int estimatedBytes = 256 + (sanitized.SafeMessage.Length * 2);
            _currentMemoryBytes += estimatedBytes;

            while (_events.Count > _capacity || _currentMemoryBytes > _maxMemoryBytes)
            {
                if (_events.Count > 0)
                {
                    var removed = _events[0];
                    _events.RemoveAt(0);
                    int removedEstimate = 256 + (removed.SafeMessage.Length * 2);
                    _currentMemoryBytes = Math.Max(0, _currentMemoryBytes - removedEstimate);
                }
                else
                {
                    _currentMemoryBytes = 0;
                    break;
                }
            }
        }
    }

    public Task<bool> FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // Memory-only sink cannot claim disk persistence (R5)
        return Task.FromResult(false);
    }

    public DiagnosticSinkHealth GetHealth()
    {
        lock (_syncLock)
        {
            return new DiagnosticSinkHealth(
                IsHealthy: true,
                TotalEmitted: Volatile.Read(ref _totalEmitted),
                TotalPersisted: 0,
                TotalSuppressed: 0,
                TotalDropped: 0,
                LastError: "Operating in memory-only mode.",
                OwnedSegmentCount: 0,
                OwnedTotalSizeBytes: 0,
                IsWriterLeaseHeld: false
            );
        }
    }

    public IReadOnlyList<DiagnosticEvent> GetRecentEvents()
    {
        lock (_syncLock)
        {
            return _events.ToArray();
        }
    }

    public bool IsEnabled(DiagnosticSeverity severity) => true;
}
