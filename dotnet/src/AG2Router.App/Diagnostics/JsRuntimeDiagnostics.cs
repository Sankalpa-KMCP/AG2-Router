using System.IO;
using System.Text.Json;

namespace AG2Router.App.Diagnostics;

public record CapturedJsError(
    string Source,
    string Message,
    string? StackTrace,
    DateTime Timestamp
);

public static class JsRuntimeDiagnostics
{
    public const int MaxRetainedErrors = 100;

    private static readonly Lazy<JsRuntimeDiagnosticsStore> Store = new(CreateProductionStore);

    public static IReadOnlyList<CapturedJsError> Errors => Store.Value.Errors;

    public static void RecordError(string source, string message, string? stackTrace = null)
        => Store.Value.RecordError(source, message, stackTrace);

    public static void Clear() => Store.Value.Clear();

    public static Task ClearAsync() => Store.Value.ClearAsync();

    public static Task FlushAsync() => Store.Value.FlushAsync();

    internal static Task<bool> DrainForShutdownAsync(TimeSpan timeout)
        => DrainForShutdownAsync(Store.Value.FlushAsync, timeout);

    internal static async Task<bool> DrainForShutdownAsync(Func<Task> flushAsync, TimeSpan timeout)
    {
        Task? flushTask = null;
        try
        {
            flushTask = flushAsync();
            await flushTask.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch
        {
            if (flushTask is not null)
            {
                _ = flushTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            // Diagnostics are best-effort during shutdown and must not prevent termination.
            return false;
        }
    }

    private static async Task PersistSnapshotAsync(
        string logPath,
        IReadOnlyList<CapturedJsError> snapshot)
    {
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(
            snapshot,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(logPath, json).ConfigureAwait(false);
    }

    private static JsRuntimeDiagnosticsStore CreateProductionStore()
        => new(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AG2-Router",
                "js_runtime_errors.json"),
            PersistSnapshotAsync);
}

internal sealed class JsRuntimeDiagnosticsStore
{
    private readonly object _syncLock = new();
    private readonly List<CapturedJsError> _errors = new();
    private readonly Queue<PersistenceOperation> _operations = new();
    private readonly string _destination;
    private readonly Func<string, IReadOnlyList<CapturedJsError>, Task> _persistAsync;

    private bool _workerRunning;
    private Exception? _pendingPersistenceFailure;

    internal JsRuntimeDiagnosticsStore(
        string destination,
        Func<string, IReadOnlyList<CapturedJsError>, Task> persistAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(persistAsync);

        _destination = destination;
        _persistAsync = persistAsync;
    }

    internal IReadOnlyList<CapturedJsError> Errors
    {
        get
        {
            lock (_syncLock)
            {
                return _errors.ToArray();
            }
        }
    }

    internal void RecordError(string source, string message, string? stackTrace = null)
    {
        lock (_syncLock)
        {
            _errors.Add(new CapturedJsError(source, message, stackTrace, DateTime.UtcNow));
            while (_errors.Count > JsRuntimeDiagnostics.MaxRetainedErrors)
            {
                _errors.RemoveAt(0);
            }

            EnqueueUnderLock(new WriteOperation(_destination, _errors.ToArray()));
        }
    }

    internal void Clear()
    {
        lock (_syncLock)
        {
            _errors.Clear();
            EnqueueUnderLock(new WriteOperation(_destination, Array.Empty<CapturedJsError>()));
        }
    }

    internal Task ClearAsync()
    {
        lock (_syncLock)
        {
            _errors.Clear();
            EnqueueUnderLock(new WriteOperation(_destination, Array.Empty<CapturedJsError>()));
            return EnqueueBarrierUnderLock();
        }
    }

    internal Task FlushAsync()
    {
        lock (_syncLock)
        {
            return EnqueueBarrierUnderLock();
        }
    }

    private Task EnqueueBarrierUnderLock()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueUnderLock(new BarrierOperation(completion));
        return completion.Task;
    }

    private void EnqueueUnderLock(PersistenceOperation operation)
    {
        _operations.Enqueue(operation);
        if (_workerRunning)
        {
            return;
        }

        _workerRunning = true;
        _ = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                PersistenceOperation operation;
                lock (_syncLock)
                {
                    if (_operations.Count == 0)
                    {
                        _workerRunning = false;
                        return;
                    }

                    operation = _operations.Dequeue();
                }

                switch (operation)
                {
                    case WriteOperation write:
                        try
                        {
                            await _persistAsync(write.Destination, write.Snapshot).ConfigureAwait(false);
                            lock (_syncLock)
                            {
                                // Each snapshot is complete, so a later successful write recovers
                                // the destination from an earlier failed attempt.
                                _pendingPersistenceFailure = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (_syncLock)
                            {
                                _pendingPersistenceFailure = ex;
                            }
                        }
                        break;

                    case BarrierOperation barrier:
                        Exception? failure;
                        lock (_syncLock)
                        {
                            failure = _pendingPersistenceFailure;
                        }

                        if (failure is null)
                        {
                            barrier.Completion.TrySetResult(true);
                        }
                        else
                        {
                            barrier.Completion.TrySetException(failure);
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            List<BarrierOperation> strandedBarriers;
            lock (_syncLock)
            {
                _pendingPersistenceFailure ??= ex;
                strandedBarriers = _operations
                    .OfType<BarrierOperation>()
                    .ToList();
                _operations.Clear();
                _workerRunning = false;
            }

            foreach (var barrier in strandedBarriers)
            {
                barrier.Completion.TrySetException(ex);
            }
        }
    }

    private abstract record PersistenceOperation;

    private sealed record WriteOperation(
        string Destination,
        IReadOnlyList<CapturedJsError> Snapshot) : PersistenceOperation;

    private sealed record BarrierOperation(
        TaskCompletionSource<bool> Completion) : PersistenceOperation;
}
