using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AG2Router.AG2.Discovery;

/// <summary>
/// Native Windows implementation of IProcessInspector.
/// Queries Win32_Process via PowerShell and netstat for listening loopback ports.
/// </summary>
public class WindowsProcessInspector : IProcessInspector
{
    private static readonly Regex NetstatRegex = new(
        @"TCP\s+(?:127\.0\.0\.1|\[::1\]):(\d+)\s+.*?LISTENING\s+(\d+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<IReadOnlyList<DiscoveredProcessRaw>> FindProcessesAsync(CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*language_server*' } | Select-Object ProcessId, Name, CommandLine, ExecutablePath | ConvertTo-Json -Compress\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to execute process enumeration: {ex.Message}", ex);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var json = (await stdoutTask.ConfigureAwait(false)).Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<DiscoveredProcessRaw>();
        }

        var results = new List<DiscoveredProcessRaw>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var p = ParseProcessElement(el);
                    if (p != null) results.Add(p);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var p = ParseProcessElement(doc.RootElement);
                if (p != null) results.Add(p);
            }
        }
        catch (JsonException)
        {
            // If JSON was malformed or unexpected output, return empty
            return Array.Empty<DiscoveredProcessRaw>();
        }

        return results;
    }

    private static DiscoveredProcessRaw? ParseProcessElement(JsonElement el)
    {
        if (!el.TryGetProperty("ProcessId", out var pidProp) || !pidProp.TryGetInt32(out var pid))
        {
            return null;
        }

        var name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
        var cmd = el.TryGetProperty("CommandLine", out var c) ? c.GetString() ?? "" : "";
        var path = el.TryGetProperty("ExecutablePath", out var ep) ? ep.GetString() ?? "" : "";

        DateTime? startTime = null;
        try
        {
            using var p = Process.GetProcessById(pid);
            startTime = p.StartTime.ToUniversalTime();
        }
        catch { }

        return new DiscoveredProcessRaw(pid, name, cmd, path, startTime);
    }

    public async Task<IReadOnlyList<int>> GetListeningPortsAsync(int processId, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netstat.exe",
            Arguments = "-ano",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to execute netstat: {ex.Message}", ex);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var output = await stdoutTask.ConfigureAwait(false);
        var ports = new HashSet<int>();

        var matches = NetstatRegex.Matches(output);
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups[2].Value, out var pid) && pid == processId)
            {
                if (int.TryParse(match.Groups[1].Value, out var port))
                {
                    ports.Add(port);
                }
            }
        }

        return ports.OrderBy(p => p).ToList();
    }

    public bool IsPidAlive(int processId, DateTime? expectedStartTime = null)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            if (proc.HasExited) return false;

            if (expectedStartTime.HasValue)
            {
                try
                {
                    var actualStart = proc.StartTime.ToUniversalTime();
                    if (actualStart != expectedStartTime.Value.ToUniversalTime())
                    {
                        return false; // PID was recycled
                    }
                }
                catch
                {
                    return false;
                }
            }

            return proc.ProcessName.Contains("language_server", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // Process not running
        }
        catch (InvalidOperationException)
        {
            return false; // Process terminated between lookup and inspection
        }
        catch (Win32Exception)
        {
            return false; // Access denied (e.g. PID recycled into privileged/system process) or process exited
        }
    }
}
