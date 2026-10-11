using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AG2Router.Core.Diagnostics;
using AG2Router.Windows.Diagnostics;

namespace AG2Router.App.Diagnostics;

/// <summary>
/// Application-owned asynchronous native diagnostic file writer enforcing Option B.
/// Enforces bounded rotation (4 MiB/segment), bounded total segments (8 segments, 32 MiB total),
/// 14-day retention, 8 KiB max event size, 512-item / 4 MiB queue bound, 256-item / 2 MiB recent buffer,
/// active writer lease file lock, durable ownership registry (Option B), Windows physical containment (Finding A),
/// verified OS file identities (Finding D), cross-run retention (Finding B), truthful retention accounting (Finding E/F),
/// truthful persistence barriers (Finding G), delayed lease reacquisition (Finding H), and bounded inspection.
/// </summary>
public sealed class BoundedJsonlDiagnosticSink : IDiagnosticSink, IAsyncDisposable, IDisposable
{
    public const int MaxSegmentSizeBytes = 4 * 1024 * 1024; // 4 MiB
    public const int MaxOwnedSegments = 8;
    public const long MaxTotalOwnedSizeBytes = 32 * 1024 * 1024; // 32 MiB
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(14);
    public const int MaxEventSizeBytes = DiagnosticEvent.MaxSerializedBytes; // 8 KiB
    public const int MaxQueueCapacity = 512;
    public const long MaxQueueMemoryBytes = 4 * 1024 * 1024; // 4 MiB
    public const int MaxRecentEvents = 256;
    public const long MaxRecentMemoryBytes = 2 * 1024 * 1024; // 2 MiB
    public const int MaxHeaderBytes = 4096; // 4 KiB limit for first-record inspection (N9)
    public const int MaxCandidatesPerPass = 64; // Bounded candidates per maintenance cycle (N9)
    public static readonly TimeSpan DefaultShutdownFlushTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultIdleMaintenanceInterval = TimeSpan.FromMinutes(5);

    private const string LockFileName = ".active_writer.lock";
    private const string FilePrefix = "diag_";
    private const string FileSuffix = ".jsonl";
    private const string SearchPattern = "diag_*.jsonl";

    private static readonly Regex OwnedSegmentFileNameRegex = new(
        @"^diag_\d{8}_\d{6}_\d{4}\.jsonl$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private readonly string _diagnosticsDirectory;
    private readonly int _maxSegmentSizeBytes;
    private readonly int _maxOwnedSegments;
    private readonly long _maxTotalOwnedSizeBytes;
    private readonly TimeSpan _retentionPeriod;
    private readonly int _queueCapacity;
    private readonly long _maxQueueMemoryBytes;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _idleMaintenanceInterval;

    private readonly Channel<QueueItem> _channel;
    private readonly Task _workerTask;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _stateLock = new();
    private readonly List<DiagnosticEvent> _recentEvents = new(MaxRecentEvents);
    private readonly List<int> _recentEventSizes = new(MaxRecentEvents);
    private long _recentMemoryBytes;

    private long _totalEmitted;
    private long _totalPersisted;
    private long _totalSuppressed;
    private long _totalDropped;
    private long _queuedMemoryBytes;
    private int _retentionFailureCount;
    private string? _lastError;
    private bool _isHealthy = true;
    private bool _disposed;

    private FileStream? _writerLockStream;
    private bool _isWriterLeaseHeld;

    private FileStream? _currentStream;
    private string? _currentFilePath;
    private long _currentSegmentBytes;
    private DateTime _currentSegmentCreatedAtUtc = DateTime.MinValue;
    private int _segmentCounter;
    private bool _workerFaultSinceLastBarrier;
    private readonly bool _isAdmittedPrimary;
    private int _reacquisitionAttempts;
    private string? _retentionCursorFileName;
    private long _currentSegmentUnflushedCount;
    private readonly DiagnosticOwnershipRegistry _ownershipRegistry;

    public bool IsDebugEnabled { get; set; } = false;
    public DiagnosticOwnershipRegistry OwnershipRegistry => _ownershipRegistry;

    public BoundedJsonlDiagnosticSink(
        string diagnosticsDirectory,
        int maxSegmentSizeBytes = MaxSegmentSizeBytes,
        int maxOwnedSegments = MaxOwnedSegments,
        long maxTotalOwnedSizeBytes = MaxTotalOwnedSizeBytes,
        TimeSpan? retentionPeriod = null,
        int queueCapacity = MaxQueueCapacity,
        long maxQueueMemoryBytes = MaxQueueMemoryBytes,
        Func<DateTime>? utcClock = null,
        TimeSpan? idleMaintenanceInterval = null,
        int lockAcquireTimeoutMs = 1000,
        bool isAdmittedPrimary = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsDirectory);
        _diagnosticsDirectory = Path.GetFullPath(diagnosticsDirectory);
        _maxSegmentSizeBytes = Math.Max(128, maxSegmentSizeBytes);
        _maxOwnedSegments = Math.Max(1, maxOwnedSegments);
        _maxTotalOwnedSizeBytes = Math.Max(128, maxTotalOwnedSizeBytes);
        _retentionPeriod = retentionPeriod ?? RetentionPeriod;
        _queueCapacity = Math.Max(1, queueCapacity);
        _maxQueueMemoryBytes = Math.Max(128, maxQueueMemoryBytes);
        _clock = utcClock ?? (() => DateTime.UtcNow);
        _idleMaintenanceInterval = idleMaintenanceInterval ?? DefaultIdleMaintenanceInterval;
        _isAdmittedPrimary = isAdmittedPrimary;

        _ownershipRegistry = new DiagnosticOwnershipRegistry(_diagnosticsDirectory);

        TryAcquireWriterLock(lockAcquireTimeoutMs);

        var options = new BoundedChannelOptions(_queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<QueueItem>(options);
        _workerTask = Task.Run(ProcessQueueAsync);
    }

    private void TryAcquireWriterLock(int timeoutMs = 1000)
    {
        try
        {
            Directory.CreateDirectory(_diagnosticsDirectory);
            var dirInfo = new DirectoryInfo(_diagnosticsDirectory);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                _isWriterLeaseHeld = false;
                _isHealthy = false;
                _lastError = "Diagnostics directory is a reparse point; writer lease rejected.";
                return;
            }

            string lockPath = Path.Combine(_diagnosticsDirectory, LockFileName);
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));

            while (true)
            {
                try
                {
                    _writerLockStream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.DeleteOnClose);
                    _isWriterLeaseHeld = true;
                    return;
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    // Controlled bounded backoff to allow shutting-down previous primary to release lock
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception ex)
        {
            _writerLockStream = null;
            _isWriterLeaseHeld = false;
            _lastError = $"Secondary instance or busy lock ({ex.GetType().Name}). Operating in memory-only mode.";
        }
    }

    private bool TryAcquireWriterLockOnce()
    {
        if (_isWriterLeaseHeld) return true;
        if (!_isAdmittedPrimary) return false;

        _reacquisitionAttempts++;
        try
        {
            Directory.CreateDirectory(_diagnosticsDirectory);
            var dirInfo = new DirectoryInfo(_diagnosticsDirectory);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            string lockPath = Path.Combine(_diagnosticsDirectory, LockFileName);
            _writerLockStream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            _isWriterLeaseHeld = true;
            lock (_stateLock)
            {
                _isHealthy = true;
                _lastError = null;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _lastError = $"Reacquisition error: {ex.GetType().Name}";
            }
            return false;
        }
    }

    private void ReleaseWriterLock()
    {
        if (_writerLockStream is not null)
        {
            try
            {
                _writerLockStream.Dispose();
            }
            catch { }
            finally
            {
                _writerLockStream = null;
                _isWriterLeaseHeld = false;
            }
        }
    }

    public bool IsEnabled(DiagnosticSeverity severity)
    {
        if (severity == DiagnosticSeverity.Debug)
        {
            return IsDebugEnabled;
        }
        return true;
    }

    public void Emit(DiagnosticEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (!IsEnabled(@event.Severity))
        {
            return;
        }

        @event = @event.Sanitize();
        Interlocked.Increment(ref _totalEmitted);

        string json;
        byte[] utf8Bytes;
        try
        {
            json = @event.ToJson();
            utf8Bytes = Encoding.UTF8.GetBytes(json + "\n");
        }
        catch
        {
            Interlocked.Increment(ref _totalDropped);
            return;
        }

        // 1. Maintain bounded in-memory buffer (256 items / 2 MiB)
        lock (_stateLock)
        {
            long eventBytes = utf8Bytes.Length;
            while ((_recentEvents.Count >= MaxRecentEvents || _recentMemoryBytes + eventBytes > MaxRecentMemoryBytes)
                   && _recentEvents.Count > 0)
            {
                _recentEvents.RemoveAt(0);
                int removedBytes = _recentEventSizes[0];
                _recentEventSizes.RemoveAt(0);
                _recentMemoryBytes = Math.Max(0, _recentMemoryBytes - removedBytes);
            }

            if (_recentEvents.Count < MaxRecentEvents && _recentMemoryBytes + eventBytes <= MaxRecentMemoryBytes)
            {
                _recentEvents.Add(@event);
                _recentEventSizes.Add(utf8Bytes.Length);
                _recentMemoryBytes += eventBytes;
            }
        }

        // 2. If writer lease is not held (secondary instance), operate strictly in memory
        if (!_isWriterLeaseHeld)
        {
            return;
        }

        // 3. Check memory bound on queue (4 MiB)
        lock (_stateLock)
        {
            if (_queuedMemoryBytes + utf8Bytes.Length > _maxQueueMemoryBytes)
            {
                Interlocked.Increment(ref _totalSuppressed);
                return;
            }
            _queuedMemoryBytes += utf8Bytes.Length;
        }

        // 4. Try non-blocking enqueue to writer worker
        var item = new EventItem(@event, utf8Bytes);
        if (!_channel.Writer.TryWrite(item))
        {
            lock (_stateLock)
            {
                _queuedMemoryBytes = Math.Max(0, _queuedMemoryBytes - utf8Bytes.Length);
            }
            Interlocked.Increment(ref _totalSuppressed);
        }
    }

    public async Task<bool> FlushAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default)
    {
        if (timeout == default)
        {
            timeout = DefaultShutdownFlushTimeout;
        }

        if (!_isWriterLeaseHeld)
        {
            return false;
        }

        if (_workerTask.IsFaulted)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = new BarrierItem(tcs);

        using var sharedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sharedCts.CancelAfter(timeout);
        try
        {
            await _channel.Writer.WriteAsync(barrier, sharedCts.Token).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(sharedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public DiagnosticSinkHealth GetHealth()
    {
        int segmentCount = 0;
        long totalSize = 0;

        if (_isWriterLeaseHeld && Directory.Exists(_diagnosticsDirectory))
        {
            try
            {
                var registered = _ownershipRegistry.GetOwnedSegments();
                foreach (var reg in registered)
                {
                    if (reg.Status != OwnedSegmentStatus.Purged)
                    {
                        segmentCount++;
                        totalSize += reg.SizeBytes;
                    }
                }
            }
            catch { }
        }

        lock (_stateLock)
        {
            return new DiagnosticSinkHealth(
                IsHealthy: _isHealthy,
                TotalEmitted: Volatile.Read(ref _totalEmitted),
                TotalPersisted: Volatile.Read(ref _totalPersisted),
                TotalSuppressed: Volatile.Read(ref _totalSuppressed),
                TotalDropped: Volatile.Read(ref _totalDropped),
                LastError: _lastError,
                OwnedSegmentCount: segmentCount,
                OwnedTotalSizeBytes: totalSize,
                IsWriterLeaseHeld: _isWriterLeaseHeld,
                RetentionFailureCount: _retentionFailureCount
            );
        }
    }

    public IReadOnlyList<DiagnosticEvent> GetRecentEvents()
    {
        lock (_stateLock)
        {
            return _recentEvents.ToArray();
        }
    }

    private async Task ProcessQueueAsync()
    {
        var reader = _channel.Reader;
        var token = _cts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                using var readTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                readTimeoutCts.CancelAfter(_idleMaintenanceInterval);

                bool hasItem;
                try
                {
                    hasItem = await reader.WaitToReadAsync(readTimeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    PerformIdleMaintenance();
                    continue;
                }

                if (!hasItem) break;

                if (!_isWriterLeaseHeld && _isAdmittedPrimary)
                {
                    TryAcquireWriterLockOnce();
                }

                while (reader.TryRead(out var item))
                {
                    switch (item)
                    {
                        case EventItem eventItem:
                            await PersistEventAsync(eventItem, _cts.Token).ConfigureAwait(false);
                            break;

                        case BarrierItem barrierItem:
                            bool flushed = await FlushCurrentStreamAsync().ConfigureAwait(false);
                            bool barrierSuccess = flushed && !_workerFaultSinceLastBarrier && _isHealthy;
                            _workerFaultSinceLastBarrier = false;
                            barrierItem.Completion.TrySetResult(barrierSuccess);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            while (reader.TryRead(out var item))
            {
                if (item is EventItem eventItem)
                {
                    await PersistEventAsync(eventItem, CancellationToken.None).ConfigureAwait(false);
                }
                else if (item is BarrierItem barrierItem)
                {
                    bool flushed = await FlushCurrentStreamAsync().ConfigureAwait(false);
                    bool barrierSuccess = flushed && !_workerFaultSinceLastBarrier && _isHealthy;
                    _workerFaultSinceLastBarrier = false;
                    barrierItem.Completion.TrySetResult(barrierSuccess);
                }
            }
            await FlushCurrentStreamAsync().ConfigureAwait(false);
        }
        finally
        {
            await CloseCurrentStreamAsync().ConfigureAwait(false);
            ReleaseWriterLock();
        }
    }

    private void PerformIdleMaintenance()
    {
        if (!_isWriterLeaseHeld && _isAdmittedPrimary)
        {
            TryAcquireWriterLockOnce();
        }

        if (!_isWriterLeaseHeld) return;

        if (_currentStream is not null && _currentSegmentCreatedAtUtc != DateTime.MinValue &&
            (_clock() - _currentSegmentCreatedAtUtc > _retentionPeriod))
        {
            CloseCurrentStreamSync();
        }

        RunRetentionCleanup();
    }

    private async Task PersistEventAsync(EventItem item, CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _queuedMemoryBytes = Math.Max(0, _queuedMemoryBytes - item.Utf8Bytes.Length);
        }

        try
        {
            EnsureOpenStream(item.Utf8Bytes.Length);

            if (_currentStream is not null)
            {
                await _currentStream.WriteAsync(item.Utf8Bytes, cancellationToken).ConfigureAwait(false);
                _currentSegmentBytes += item.Utf8Bytes.Length;
                Interlocked.Increment(ref _currentSegmentUnflushedCount);
            }
            else
            {
                _workerFaultSinceLastBarrier = true;
                Interlocked.Increment(ref _totalDropped);
            }
        }
        catch (Exception ex)
        {
            _workerFaultSinceLastBarrier = true;
            Interlocked.Increment(ref _totalDropped);
            lock (_stateLock)
            {
                _isHealthy = false;
                _lastError = $"Persistence error: {ex.GetType().Name}";
            }
            await CloseCurrentStreamAsync().ConfigureAwait(false);
        }
    }

    private void EnsureOpenStream(int requiredNextBytes)
    {
        bool isSizeExceeded = _currentStream is not null && (_currentSegmentBytes + requiredNextBytes > _maxSegmentSizeBytes);
        bool isAgeExceeded = _currentStream is not null && (_currentSegmentCreatedAtUtc != DateTime.MinValue) &&
                             (_clock() - _currentSegmentCreatedAtUtc > _retentionPeriod);

        if (isSizeExceeded || isAgeExceeded)
        {
            CloseCurrentStreamSync();
        }

        if (_currentStream is null)
        {
            OpenNewSegment();
            RunRetentionCleanup();
        }
    }

    private void OpenNewSegment()
    {
        FileStream? stream = null;
        try
        {
            Directory.CreateDirectory(_diagnosticsDirectory);
            var dirInfo = new DirectoryInfo(_diagnosticsDirectory);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("Diagnostics directory is a reparse point; file creation rejected.");
            }

            string timestamp = _clock().ToString("yyyyMMdd_HHmmss");
            int counter = Interlocked.Increment(ref _segmentCounter);
            string segmentId = $"{FilePrefix}{timestamp}_{counter:D4}";
            string filename = $"{segmentId}{FileSuffix}";
            string fullPath = Path.Combine(_diagnosticsDirectory, filename);

            if (!IsPathWithinDirectory(fullPath, _diagnosticsDirectory))
            {
                throw new InvalidOperationException("Computed segment path escapes diagnostics directory.");
            }

            Interlocked.Exchange(ref _currentSegmentUnflushedCount, 0);
            _currentFilePath = fullPath;
            _currentSegmentCreatedAtUtc = _clock();
            stream = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 16 * 1024,
                useAsync: true);

            var header = new DiagnosticSegmentHeader
            {
                RunId = DiagnosticRunContext.RunId,
                SegmentId = segmentId,
                CreatedAtUtc = _currentSegmentCreatedAtUtc.ToString("o")
            };

            byte[] headerBytes = Encoding.UTF8.GetBytes(header.ToJson() + "\n");
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Flush(flushToDisk: true);
            _currentSegmentBytes = headerBytes.Length;
            _currentStream = stream;

            uint volSerial = 0, fileIdxHigh = 0, fileIdxLow = 0;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                WindowsHandleFileDeletion.TryGetFileIdentity(stream.SafeFileHandle, out volSerial, out fileIdxHigh, out fileIdxLow);
            }

            _ownershipRegistry.RegisterSegment(new OwnedSegmentRecord
            {
                SegmentId = segmentId,
                FileName = filename,
                RunId = DiagnosticRunContext.RunId,
                CreatedAtUtc = _currentSegmentCreatedAtUtc.ToString("o"),
                VolumeSerialNumber = volSerial,
                FileIndexHigh = fileIdxHigh,
                FileIndexLow = fileIdxLow,
                SizeBytes = headerBytes.Length,
                Status = OwnedSegmentStatus.Active
            });
        }
        catch (Exception ex)
        {
            if (stream != null)
            {
                try { stream.Dispose(); } catch { }
            }
            _currentStream = null;
            _currentFilePath = null;
            _workerFaultSinceLastBarrier = true;
            lock (_stateLock)
            {
                _isHealthy = false;
                _lastError = $"Failed to open segment file: {ex.GetType().Name}";
            }
        }
    }

    private async Task<bool> FlushCurrentStreamAsync()
    {
        if (_currentStream is not null)
        {
            try
            {
                await _currentStream.FlushAsync(_cts.Token).ConfigureAwait(false);
                _currentStream.Flush(flushToDisk: true);
                long flushedCount = Interlocked.Exchange(ref _currentSegmentUnflushedCount, 0);
                Interlocked.Add(ref _totalPersisted, flushedCount);
                if (_currentFilePath != null)
                {
                    string segId = Path.GetFileNameWithoutExtension(_currentFilePath);
                    _ownershipRegistry.UpdateSegmentStatus(segId, OwnedSegmentStatus.Active, _currentSegmentBytes);
                }
                RunRetentionCleanup();
                return true;
            }
            catch (Exception ex)
            {
                // Discard unconfirmed counts on failure; never transfer to another segment (Finding G)
                Interlocked.Exchange(ref _currentSegmentUnflushedCount, 0);
                _workerFaultSinceLastBarrier = true;
                lock (_stateLock)
                {
                    _isHealthy = false;
                    _lastError = $"Flush error: {ex.GetType().Name}";
                }
                return false;
            }
        }
        return true;
    }

    private async Task CloseCurrentStreamAsync()
    {
        if (_currentStream is not null)
        {
            var streamToClose = _currentStream;
            string? filePathToClose = _currentFilePath;
            long segBytesToClose = _currentSegmentBytes;

            _currentStream = null;
            _currentFilePath = null;
            _currentSegmentBytes = 0;
            try
            {
                await streamToClose.FlushAsync().ConfigureAwait(false);
                streamToClose.Flush(flushToDisk: true);
                long flushedCount = Interlocked.Exchange(ref _currentSegmentUnflushedCount, 0);
                Interlocked.Add(ref _totalPersisted, flushedCount);
                if (filePathToClose != null)
                {
                    string segId = Path.GetFileNameWithoutExtension(filePathToClose);
                    _ownershipRegistry.UpdateSegmentStatus(segId, OwnedSegmentStatus.Closed, segBytesToClose);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _currentSegmentUnflushedCount, 0);
                _workerFaultSinceLastBarrier = true;
                lock (_stateLock)
                {
                    _isHealthy = false;
                    _lastError = $"Stream flush on close error: {ex.GetType().Name}";
                }
            }
            finally
            {
                try
                {
                    await streamToClose.DisposeAsync().ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private void CloseCurrentStreamSync()
    {
        if (_currentStream is not null)
        {
            var streamToClose = _currentStream;
            string? filePathToClose = _currentFilePath;
            long segBytesToClose = _currentSegmentBytes;

            _currentStream = null;
            _currentFilePath = null;
            _currentSegmentBytes = 0;
            try
            {
                streamToClose.Flush(flushToDisk: true);
                long flushedCount = Interlocked.Exchange(ref _currentSegmentUnflushedCount, 0);
                Interlocked.Add(ref _totalPersisted, flushedCount);
                if (filePathToClose != null)
                {
                    string segId = Path.GetFileNameWithoutExtension(filePathToClose);
                    _ownershipRegistry.UpdateSegmentStatus(segId, OwnedSegmentStatus.Closed, segBytesToClose);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _currentSegmentUnflushedCount, 0);
                _workerFaultSinceLastBarrier = true;
                lock (_stateLock)
                {
                    _isHealthy = false;
                    _lastError = $"Stream flush on close error: {ex.GetType().Name}";
                }
            }
            finally
            {
                try
                {
                    streamToClose.Dispose();
                }
                catch { }
            }
        }
    }

    private void RunRetentionCleanup()
    {
        if (!_isWriterLeaseHeld) return;

        try
        {
            if (!Directory.Exists(_diagnosticsDirectory))
            {
                return;
            }

            var dirInfo = new DirectoryInfo(_diagnosticsDirectory);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                lock (_stateLock)
                {
                    _isHealthy = false;
                    _lastError = "Diagnostics directory is a reparse point; retention cleanup aborted.";
                }
                return;
            }

            // Gather candidate files:
            // Combines registered owned segments and disk candidate files matching diag_*.jsonl
            var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in _ownershipRegistry.GetOwnedSegments())
            {
                if (rec.Status != OwnedSegmentStatus.Purged)
                {
                    candidatePaths.Add(Path.Combine(_diagnosticsDirectory, rec.FileName));
                }
            }
            if (Directory.Exists(_diagnosticsDirectory))
            {
                foreach (var diskPath in Directory.EnumerateFiles(_diagnosticsDirectory, SearchPattern))
                {
                    candidatePaths.Add(diskPath);
                }
            }

            var activeCandidates = new List<FileInfo>();
            foreach (var path in candidatePaths)
            {
                if (File.Exists(path) && !string.Equals(path, _currentFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(path);
                    if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint) && fi.LinkTarget == null)
                    {
                        activeCandidates.Add(fi);
                    }
                }
            }

            if (activeCandidates.Count == 0) return;

            activeCandidates.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // Cursor progression: bounded to MaxCandidatesPerPass (64)
            List<FileInfo> eligibleFiles;
            if (string.IsNullOrEmpty(_retentionCursorFileName))
            {
                eligibleFiles = activeCandidates.Take(MaxCandidatesPerPass).ToList();
            }
            else
            {
                eligibleFiles = activeCandidates
                    .Where(f => string.Compare(f.Name, _retentionCursorFileName, StringComparison.OrdinalIgnoreCase) > 0)
                    .Take(MaxCandidatesPerPass)
                    .ToList();

                if (eligibleFiles.Count < MaxCandidatesPerPass)
                {
                    int remaining = MaxCandidatesPerPass - eligibleFiles.Count;
                    var wrapAround = activeCandidates
                        .Take(remaining)
                        .Where(f => !eligibleFiles.Any(e => string.Equals(e.FullName, f.FullName, StringComparison.OrdinalIgnoreCase)));
                    eligibleFiles.AddRange(wrapAround);
                }
            }

            if (eligibleFiles.Count > 0)
            {
                _retentionCursorFileName = eligibleFiles.Last().Name;
            }

            var cutoffUtc = _clock() - _retentionPeriod;

            // 1. Retention by age
            for (int i = eligibleFiles.Count - 1; i >= 0; i--)
            {
                var file = eligibleFiles[i];
                if (file.CreationTimeUtc < cutoffUtc)
                {
                    if (TryDeleteOwnedFile(file))
                    {
                        eligibleFiles.RemoveAt(i);
                        activeCandidates.Remove(file);
                    }
                    else
                    {
                        _retentionFailureCount++;
                    }
                }
            }

            // 2. Retention by count
            int maxClosedSegments = _currentFilePath != null ? Math.Max(0, _maxOwnedSegments - 1) : _maxOwnedSegments;
            var skippedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (activeCandidates.Count > maxClosedSegments)
            {
                var oldest = eligibleFiles.FirstOrDefault(f => !skippedCandidates.Contains(f.FullName));
                if (oldest == null) break;

                if (TryDeleteOwnedFile(oldest))
                {
                    eligibleFiles.Remove(oldest);
                    activeCandidates.Remove(oldest);
                }
                else
                {
                    skippedCandidates.Add(oldest.FullName);
                    eligibleFiles.Remove(oldest);
                    activeCandidates.Remove(oldest);
                    _retentionFailureCount++;
                }
            }

            // 3. Retention by total size
            long totalSize = activeCandidates.Sum(f => f.Length);
            while (totalSize > _maxTotalOwnedSizeBytes && activeCandidates.Count > 1)
            {
                var oldest = eligibleFiles.FirstOrDefault(f => !skippedCandidates.Contains(f.FullName));
                if (oldest == null) break;

                long len = oldest.Length;
                if (TryDeleteOwnedFile(oldest))
                {
                    eligibleFiles.Remove(oldest);
                    activeCandidates.Remove(oldest);
                    totalSize -= len;
                }
                else
                {
                    skippedCandidates.Add(oldest.FullName);
                    eligibleFiles.Remove(oldest);
                    activeCandidates.Remove(oldest);
                    totalSize -= len;
                    _retentionFailureCount++;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _isHealthy = false;
                _lastError = $"Retention cleanup error: {ex.GetType().Name}";
            }
        }
    }

    internal bool TryDeleteOwnedFile(FileInfo file)
    {
        try
        {
            file.Refresh();
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LinkTarget != null)
            {
                return false;
            }

            if (!OwnedSegmentFileNameRegex.IsMatch(file.Name))
            {
                return false;
            }

            string segId = Path.GetFileNameWithoutExtension(file.Name);
            bool deleted = WindowsHandleFileDeletion.TryDeleteOwnedFile(
                file.FullName,
                _diagnosticsDirectory,
                (stream, volSerial, fileIdxHigh, fileIdxLow) =>
                    ValidateSegmentStreamOwnership(stream, file.Name, volSerial, fileIdxHigh, fileIdxLow));

            file.Refresh();
            if (deleted && !file.Exists)
            {
                _ownershipRegistry.RemoveSegment(segId);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _isHealthy = false;
                _lastError = $"File deletion failed: {ex.GetType().Name}";
            }
            return false;
        }
    }

    public static bool ValidateSegmentStreamOwnership(Stream stream, string fileName)
    {
        return ValidateSegmentStreamOwnershipStatic(stream, fileName, registry: null);
    }

    public bool ValidateSegmentStreamOwnership(
        Stream stream,
        string fileName,
        uint volumeSerial = 0,
        uint fileIndexHigh = 0,
        uint fileIndexLow = 0)
    {
        try
        {
            if (stream.Length == 0) return false;

            byte[] buffer = new byte[MaxHeaderBytes];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0) return false;

            int newlineIndex = Array.IndexOf(buffer, (byte)'\n', 0, bytesRead);
            if (newlineIndex < 0 && bytesRead == MaxHeaderBytes)
            {
                return false;
            }

            int lineLen = newlineIndex >= 0 ? newlineIndex : bytesRead;
            string headerJson = Encoding.UTF8.GetString(buffer, 0, lineLen).Trim();
            if (string.IsNullOrWhiteSpace(headerJson)) return false;

            var header = DiagnosticSegmentHeader.FromJson(headerJson);
            if (header is null) return false;

            string expectedSegmentId = Path.GetFileNameWithoutExtension(fileName);
            if (!string.Equals(header.SegmentId, expectedSegmentId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(header.Format, DiagnosticSegmentHeader.CurrentFormat, StringComparison.Ordinal) ||
                header.SchemaVersion != DiagnosticEvent.CurrentSchemaVersion ||
                !DiagnosticSanitizer.IsValidIdentifier(header.RunId))
            {
                return false;
            }

            // Option B: Verify ownership against durable registry and authoritative OS file identity
            if (_ownershipRegistry.TryGetSegment(expectedSegmentId, out var record) && record is not null)
            {
                if (record.Status == OwnedSegmentStatus.Purged)
                {
                    return false;
                }

                // On Windows, verify authoritative physical OS file identity
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && volumeSerial != 0)
                {
                    if (record.VolumeSerialNumber != volumeSerial ||
                        record.FileIndexHigh != fileIndexHigh ||
                        record.FileIndexLow != fileIndexLow)
                    {
                        // Replaced file or identity mismatch! (Section 4, Finding D)
                        return false;
                    }
                }

                return true;
            }

            // Fallback for pre-existing or test-synthesized segments without registry entry:
            // Must have matching RunId from the active run context
            if (string.Equals(header.RunId, DiagnosticRunContext.RunId, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool ValidateSegmentStreamOwnershipStatic(
        Stream stream,
        string fileName,
        DiagnosticOwnershipRegistry? registry = null,
        uint volumeSerial = 0,
        uint fileIndexHigh = 0,
        uint fileIndexLow = 0)
    {
        try
        {
            if (stream.Length == 0) return false;

            byte[] buffer = new byte[MaxHeaderBytes];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0) return false;

            int newlineIndex = Array.IndexOf(buffer, (byte)'\n', 0, bytesRead);
            if (newlineIndex < 0 && bytesRead == MaxHeaderBytes) return false;

            int lineLen = newlineIndex >= 0 ? newlineIndex : bytesRead;
            string headerJson = Encoding.UTF8.GetString(buffer, 0, lineLen).Trim();
            if (string.IsNullOrWhiteSpace(headerJson)) return false;

            var header = DiagnosticSegmentHeader.FromJson(headerJson);
            if (header is null) return false;

            string expectedSegmentId = Path.GetFileNameWithoutExtension(fileName);
            if (!string.Equals(header.SegmentId, expectedSegmentId, StringComparison.OrdinalIgnoreCase)) return false;

            if (!string.Equals(header.Format, DiagnosticSegmentHeader.CurrentFormat, StringComparison.Ordinal) ||
                header.SchemaVersion != DiagnosticEvent.CurrentSchemaVersion ||
                !DiagnosticSanitizer.IsValidIdentifier(header.RunId))
            {
                return false;
            }

            if (registry != null)
            {
                if (!registry.TryGetSegment(expectedSegmentId, out var record) || record is null) return false;
                if (record.Status == OwnedSegmentStatus.Purged) return false;
                if (volumeSerial != 0 && (record.VolumeSerialNumber != volumeSerial || record.FileIndexHigh != fileIndexHigh || record.FileIndexLow != fileIndexLow))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathWithinDirectory(string fullPath, string baseDirectory)
    {
        string normalizedBase = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedPath = Path.GetFullPath(fullPath);
        return normalizedPath.StartsWith(normalizedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.Writer.TryComplete();
        _cts.CancelAfter(DefaultShutdownFlushTimeout);

        try
        {
            await _workerTask.WaitAsync(DefaultShutdownFlushTimeout).ConfigureAwait(false);
            _cts.Dispose();
        }
        catch (TimeoutException)
        {
            _ = _workerTask.ContinueWith(t =>
            {
                _ = t.Exception;
                try { _cts.Dispose(); } catch { }
            }, TaskContinuationOptions.None);

            lock (_stateLock)
            {
                _isHealthy = false;
                _lastError = "Writer shutdown wait timed out after 2 seconds.";
            }
        }
        catch (Exception ex)
        {
            _ = _workerTask.ContinueWith(t =>
            {
                _ = t.Exception;
                try { _cts.Dispose(); } catch { }
            }, TaskContinuationOptions.None);

            lock (_stateLock)
            {
                _isHealthy = false;
                _lastError = $"Writer shutdown error: {ex.GetType().Name}";
            }
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private abstract record QueueItem;
    private sealed record EventItem(DiagnosticEvent Event, byte[] Utf8Bytes) : QueueItem;
    private sealed record BarrierItem(TaskCompletionSource<bool> Completion) : QueueItem;
}
