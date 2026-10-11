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
    public const int MaxRetainedBytes = 64 * 1024; // 64 KiB total bound (R3)

    private static readonly AsyncLocal<JsRuntimeDiagnosticsStore?> TestStoreOverride = new();
    private static JsRuntimeDiagnosticsStore? _productionStore;
    private static readonly Lazy<JsRuntimeDiagnosticsStore> InMemoryFallbackStore = new(CreateInMemoryStore);

    private static JsRuntimeDiagnosticsStore CreateInMemoryStore()
        => new(":memory:", static (_, _) => Task.CompletedTask, allowProductionPath: true);

    internal static JsRuntimeDiagnosticsStore EffectiveStore
        => TestStoreOverride.Value ?? _productionStore ?? InMemoryFallbackStore.Value;

    public static IReadOnlyList<CapturedJsError> Errors => EffectiveStore.Errors;

    public static void InitializeProductionStore()
    {
        if (_productionStore is not null) return;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) return;
        var path = Path.Combine(localAppData, "AG2-Router", "js_runtime_errors.json");
        _productionStore = new JsRuntimeDiagnosticsStore(path, PersistSnapshotAsync, allowProductionPath: true);
    }

    internal static IDisposable UseScopedStoreForTesting(JsRuntimeDiagnosticsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        AssertNotProductionAppData(store.Destination);
        var previous = TestStoreOverride.Value;
        TestStoreOverride.Value = store;
        return new StoreScope(() => TestStoreOverride.Value = previous);
    }

    internal static void ResetForTesting()
    {
        TestStoreOverride.Value = null;
        _productionStore = null;
    }

    internal static void AssertNotProductionAppData(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:") return;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) return;
        var prodDir = Path.GetFullPath(Path.Combine(localAppData, "AG2-Router"));
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(prodDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Safety invariant violated: Diagnostic store attempted to target production AppData path: {path}");
        }
    }

    private sealed class StoreScope(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    private static readonly HashSet<string> KnownJsErrorTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TypeError", "ReferenceError", "SyntaxError", "RangeError", "URIError",
        "EvalError", "NetworkError", "SecurityError", "UnhandledPromiseRejection", "DOMException"
    };

    public static string ClassifyJsError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "UnknownError";

        string trimmed = message.Trim();
        int colonIdx = trimmed.IndexOf(':');
        string candidateType = (colonIdx > 0 ? trimmed[..colonIdx] : trimmed).Trim();

        foreach (var known in KnownJsErrorTypes)
        {
            if (candidateType.StartsWith(known, StringComparison.OrdinalIgnoreCase) ||
                candidateType.EndsWith(known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        if (trimmed.Contains("Network", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("fetch", StringComparison.OrdinalIgnoreCase))
        {
            return "NetworkError";
        }

        if (trimmed.Contains("Security", StringComparison.OrdinalIgnoreCase))
        {
            return "SecurityError";
        }

        return "UncaughtError";
    }

    private static readonly HashSet<string> TrustedJsSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dashboard", "AppBundle", "InlineScript", "ExternalScript"
    };

    private static readonly System.Text.RegularExpressions.Regex LineColRegex = new(
        @"(?:line|:)\s*(\d+)(?:\s*(?:col|column|:)\s*(\d+))?",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    public static string ClassifyJsSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "Dashboard";

        string trimmed = source.Trim();
        if (TrustedJsSources.Contains(trimmed)) return trimmed;

        if (trimmed.Contains("bundle", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            return "AppBundle";
        }

        if (trimmed.Contains("inline", StringComparison.OrdinalIgnoreCase))
        {
            return "InlineScript";
        }

        if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("chrome", StringComparison.OrdinalIgnoreCase))
        {
            return "Dashboard";
        }

        return "ExternalScript";
    }

    public static string? ClassifyJsStackTrace(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return null;

        try
        {
            var match = LineColRegex.Match(stackTrace);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int line))
            {
                if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out int col))
                {
                    return $"Line:{line},Col:{col}";
                }
                return $"Line:{line}";
            }
        }
        catch
        {
            // Bounded regex timeout
        }

        return null;
    }

    public static void RecordError(string source, string message, string? stackTrace = null)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 8192 || (stackTrace != null && stackTrace.Length > 8192))
        {
            try
            {
                DiagnosticSession.Emit(
                    eventId: Core.Diagnostics.DiagnosticEventCatalog.JavaScriptCaptureRejected,
                    component: Core.Diagnostics.DiagnosticComponents.Dashboard,
                    operation: Core.Diagnostics.DiagnosticOperations.CaptureJavaScriptError,
                    outcome: Core.Diagnostics.DiagnosticOutcome.Failure,
                    failureCategory: Core.Diagnostics.DiagnosticFailureCategory.UncaughtException,
                    safeMessage: "JavaScript error capture rejected due to empty or oversized payload.",
                    originSymbol: Core.Diagnostics.DiagnosticSanitizer.SanitizeSymbol(source, 64)
                );
            }
            catch { }
            return;
        }

        string safeCategory = ClassifyJsError(message);
        string safeSource = ClassifyJsSource(source);
        string? safeStack = ClassifyJsStackTrace(stackTrace);

        EffectiveStore.RecordError(safeSource, safeCategory, safeStack);

        try
        {
            DiagnosticSession.Emit(
                eventId: Core.Diagnostics.DiagnosticEventCatalog.JavaScriptException,
                component: Core.Diagnostics.DiagnosticComponents.Dashboard,
                operation: Core.Diagnostics.DiagnosticOperations.JavaScriptRuntimeError,
                outcome: Core.Diagnostics.DiagnosticOutcome.Failure,
                failureCategory: Core.Diagnostics.DiagnosticFailureCategory.UncaughtException,
                safeMessage: Core.Diagnostics.DiagnosticEventCatalog.GetSafeDescription(Core.Diagnostics.DiagnosticEventCatalog.JavaScriptException),
                originSymbol: safeSource,
                severity: Core.Diagnostics.DiagnosticSeverity.Warning
            );
        }
        catch
        {
            // Diagnostics must never throw
        }
    }

    public static void Clear() => EffectiveStore.Clear();

    public static Task ClearAsync() => EffectiveStore.ClearAsync();

    public static Task FlushAsync() => EffectiveStore.FlushAsync();

    internal static Task<bool> DrainForShutdownAsync(TimeSpan timeout)
        => DrainForShutdownAsync(EffectiveStore.FlushAsync, timeout);

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
        if (string.IsNullOrWhiteSpace(logPath) || logPath == ":memory:")
        {
            return;
        }

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
}

internal sealed class JsRuntimeDiagnosticsStore
{
    private readonly object _syncLock = new();
    private readonly List<CapturedJsError> _errors = new();
    private readonly LinkedList<PersistenceOperation> _operations = new();
    private readonly string _destination;
    private readonly Func<string, IReadOnlyList<CapturedJsError>, Task> _persistAsync;

    private long _currentErrorBytes;
    private bool _workerRunning;
    private Exception? _pendingPersistenceFailure;

    internal string Destination => _destination;
    internal int PendingOperationsCount { get { lock (_syncLock) return _operations.Count; } }

    internal JsRuntimeDiagnosticsStore(
        string destination,
        Func<string, IReadOnlyList<CapturedJsError>, Task> persistAsync,
        bool allowProductionPath = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(persistAsync);

        if (!allowProductionPath)
        {
            JsRuntimeDiagnostics.AssertNotProductionAppData(destination);
        }

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
        string safeSource = Core.Diagnostics.DiagnosticSanitizer.SanitizeSymbol(source, 64) ?? "Unknown";
        string safeMsg = Core.Diagnostics.DiagnosticSanitizer.SanitizeText(message, 256);
        string? safeStack = !string.IsNullOrWhiteSpace(stackTrace)
            ? Core.Diagnostics.DiagnosticSanitizer.SanitizeText(stackTrace, 512)
            : null;

        lock (_syncLock)
        {
            int entryBytes = (safeSource.Length + safeMsg.Length + (safeStack?.Length ?? 0)) * 2 + 64;
            _currentErrorBytes += entryBytes;
            _errors.Add(new CapturedJsError(safeSource, safeMsg, safeStack, DateTime.UtcNow));

            // Enforce record count and total byte bounds (R3)
            while (_errors.Count > JsRuntimeDiagnostics.MaxRetainedErrors || _currentErrorBytes > JsRuntimeDiagnostics.MaxRetainedBytes)
            {
                if (_errors.Count > 0)
                {
                    var oldest = _errors[0];
                    int oldBytes = (oldest.Source.Length + oldest.Message.Length + (oldest.StackTrace?.Length ?? 0)) * 2 + 64;
                    _errors.RemoveAt(0);
                    _currentErrorBytes = Math.Max(0, _currentErrorBytes - oldBytes);
                }
                else
                {
                    _currentErrorBytes = 0;
                    break;
                }
            }

            EnqueueUnderLock(new WriteOperation(_destination, _errors.ToArray()));
        }
    }

    internal void Clear()
    {
        lock (_syncLock)
        {
            _errors.Clear();
            _currentErrorBytes = 0;
            EnqueueUnderLock(new WriteOperation(_destination, Array.Empty<CapturedJsError>()));
        }
    }

    internal Task ClearAsync()
    {
        lock (_syncLock)
        {
            _errors.Clear();
            _currentErrorBytes = 0;
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
        if (operation is WriteOperation writeOp)
        {
            // Coalesce consecutive WriteOperations to prevent unbounded producer backlog (R3)
            if (_operations.Count > 0 && _operations.Last?.Value is WriteOperation)
            {
                _operations.Last.Value = writeOp;
                return;
            }
        }

        _operations.AddLast(operation);
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

                    operation = _operations.First!.Value;
                    _operations.RemoveFirst();
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
