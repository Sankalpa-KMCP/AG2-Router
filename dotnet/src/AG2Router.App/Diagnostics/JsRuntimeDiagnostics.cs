using System.Collections.Concurrent;
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
    private static readonly ConcurrentBag<CapturedJsError> _errors = new();

    public static IReadOnlyList<CapturedJsError> Errors => _errors.ToList();

    public static void RecordError(string source, string message, string? stackTrace = null)
    {
        var err = new CapturedJsError(source, message, stackTrace, DateTime.UtcNow);
        _errors.Add(err);

        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AG2-Router",
                "js_runtime_errors.json"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, JsonSerializer.Serialize(_errors.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Logging best-effort
        }
    }

    public static void Clear()
    {
        _errors.Clear();
    }
}
