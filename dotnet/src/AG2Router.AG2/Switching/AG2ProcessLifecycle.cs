using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AG2Router.AG2.Discovery;
using AG2Router.AG2.Security;

namespace AG2Router.AG2.Switching;

public sealed record AG2ProcessSnapshot(
    int ProcessId,
    DateTime StartTimeUtc,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> SanitizedArguments,
    long ExecutableLength,
    DateTime ExecutableLastWriteUtc,
    long DetectorGeneration);

public sealed record AG2ProcessGeneration(
    int ProcessId,
    DateTime StartTimeUtc,
    string ExecutablePath,
    long DetectorGeneration);

public interface IAG2ProcessLifecycle
{
    Task<AG2ProcessSnapshot> CaptureVerifiedAsync(CancellationToken cancellationToken = default);
    Task RevalidateAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default);
    Task StopVerifiedAsync(AG2ProcessSnapshot snapshot, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<int> LaunchAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<AG2ProcessGeneration> WaitForHealthyReplacementAsync(
        AG2ProcessSnapshot original,
        int launchedProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
    Task<AG2ProcessGeneration> RestoreAsync(
        AG2ProcessSnapshot original,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
    Task<bool> IsGenerationCurrentAsync(AG2ProcessGeneration generation, CancellationToken cancellationToken = default);
}

public sealed class AG2ProcessLifecycleException : Exception
{
    public AG2ProcessLifecycleException(string message) : base(message) { }
    public AG2ProcessLifecycleException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Transitions only the one strictly proven Antigravity language-server process.
/// It never searches or kills by basename alone and never reconstructs launch flags.
/// </summary>
public sealed class WindowsAG2ProcessLifecycle : IAG2ProcessLifecycle
{
    private readonly IProcessInspector _inspector;
    private readonly AG2ProcessDetector _detector;

    public WindowsAG2ProcessLifecycle(AG2ProcessDetector detector, IProcessInspector? inspector = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _inspector = inspector ?? new WindowsProcessInspector();
    }

    public async Task<AG2ProcessSnapshot> CaptureVerifiedAsync(CancellationToken cancellationToken = default)
    {
        var processes = await _inspector.FindProcessesAsync(cancellationToken).ConfigureAwait(false);
        var validation = ProcessProvenanceValidator.Validate(processes);
        if (validation.Status != ProvenanceStatus.Discovered || validation.SelectedProcess == null)
        {
            throw new AG2ProcessLifecycleException($"Unsafe process discovery: {validation.Message}");
        }

        var process = validation.SelectedProcess;
        if (process.StartTime == null || string.IsNullOrWhiteSpace(process.ExecutablePath) ||
            string.IsNullOrWhiteSpace(process.CommandLine))
        {
            throw new AG2ProcessLifecycleException("Verified process is missing start-time, executable, or command-line evidence.");
        }

        string executable = Path.GetFullPath(process.ExecutablePath);
        if (!File.Exists(executable))
        {
            throw new AG2ProcessLifecycleException("Verified Antigravity executable no longer exists.");
        }

        var info = new FileInfo(executable);
        var arguments = ParseWindowsCommandLine(process.CommandLine, executable);
        return new AG2ProcessSnapshot(
            process.ProcessId,
            process.StartTime.Value.ToUniversalTime(),
            executable,
            arguments,
            SanitizeArguments(arguments),
            info.Length,
            info.LastWriteTimeUtc,
            _detector.CurrentGeneration);
    }

    public async Task RevalidateAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var current = await CaptureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        if (!SameProcess(snapshot, current))
        {
            throw new AG2ProcessLifecycleException("Verified Antigravity process generation changed before transition.");
        }
    }

    public async Task StopVerifiedAsync(
        AG2ProcessSnapshot snapshot,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        await RevalidateAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (!_inspector.IsPidAlive(snapshot.ProcessId, snapshot.StartTimeUtc))
        {
            throw new AG2ProcessLifecycleException("Verified process exited before it could be stopped safely.");
        }

        try
        {
            using var process = Process.GetProcessById(snapshot.ProcessId);
            process.Kill(entireProcessTree: false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AG2ProcessLifecycleException("Timed out waiting for the verified Antigravity process to exit.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            throw new AG2ProcessLifecycleException("Failed to stop the verified Antigravity process.", ex);
        }

        if (_inspector.IsPidAlive(snapshot.ProcessId, snapshot.StartTimeUtc))
        {
            throw new AG2ProcessLifecycleException("Verified Antigravity process remained alive after termination.");
        }
        _detector.PrepareForProcessTransition();
    }

    public Task<int> LaunchAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(snapshot.ExecutablePath);
        if (!info.Exists || info.Length != snapshot.ExecutableLength ||
            info.LastWriteTimeUtc != snapshot.ExecutableLastWriteUtc)
        {
            throw new AG2ProcessLifecycleException("Captured Antigravity executable changed after process snapshot.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = snapshot.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string argument in snapshot.Arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            var process = Process.Start(startInfo)
                ?? throw new AG2ProcessLifecycleException("Antigravity process launch returned no process handle.");
            int pid = process.Id;
            process.Dispose();
            return Task.FromResult(pid);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new AG2ProcessLifecycleException("Failed to launch the captured Antigravity executable.", ex);
        }
    }

    public async Task<AG2ProcessGeneration> WaitForHealthyReplacementAsync(
        AG2ProcessSnapshot original,
        int launchedProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _detector.PrepareForProcessTransition();
            var discovery = await _detector.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var session = _detector.GetCachedSession();
            if (discovery.IsRunning && session != null)
            {
                if (session.Pid != launchedProcessId || session.Pid == original.ProcessId || session.StartTime == null ||
                    !PathEquals(session.BinaryPath, original.ExecutablePath) ||
                    session.StartTime.Value.ToUniversalTime() <= original.StartTimeUtc)
                {
                    throw new AG2ProcessLifecycleException("Rediscovery selected an unexpected or stale process generation.");
                }

                return new AG2ProcessGeneration(
                    session.Pid,
                    session.StartTime.Value.ToUniversalTime(),
                    Path.GetFullPath(session.BinaryPath!),
                    session.Generation);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
        throw new AG2ProcessLifecycleException("Timed out rediscovering a healthy replacement Antigravity process.");
    }

    public async Task<AG2ProcessGeneration> RestoreAsync(
        AG2ProcessSnapshot original,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var processes = await _inspector.FindProcessesAsync(cancellationToken).ConfigureAwait(false);
        var validation = ProcessProvenanceValidator.Validate(processes);
        if (validation.Status == ProvenanceStatus.Discovered && validation.SelectedProcess != null)
        {
            var selected = validation.SelectedProcess;
            if (selected.ProcessId == original.ProcessId && selected.StartTime != null &&
                SameStart(selected.StartTime.Value, original.StartTimeUtc) &&
                PathEquals(selected.ExecutablePath, original.ExecutablePath))
            {
                _detector.PrepareForProcessTransition();
                await _detector.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                var same = _detector.GetCachedSession()
                    ?? throw new AG2ProcessLifecycleException("Original process could not be reconnected during rollback.");
                return new AG2ProcessGeneration(same.Pid, same.StartTime!.Value, Path.GetFullPath(same.BinaryPath!), same.Generation);
            }

            var current = await CaptureVerifiedAsync(cancellationToken).ConfigureAwait(false);
            await StopVerifiedAsync(current, timeout, cancellationToken).ConfigureAwait(false);
        }
        else if (validation.Status != ProvenanceStatus.NoCandidates)
        {
            throw new AG2ProcessLifecycleException($"Rollback process discovery is unsafe: {validation.Message}");
        }

        _detector.PrepareForProcessTransition();
        int launchedPid = await LaunchAsync(original, cancellationToken).ConfigureAwait(false);
        return await WaitForHealthyReplacementAsync(original, launchedPid, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<bool> IsGenerationCurrentAsync(
        AG2ProcessGeneration generation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = _detector.GetCachedSession();
        bool current = cached != null && cached.Generation == generation.DetectorGeneration &&
            cached.Pid == generation.ProcessId && cached.StartTime != null &&
            SameStart(cached.StartTime.Value, generation.StartTimeUtc) &&
            PathEquals(cached.BinaryPath, generation.ExecutablePath);
        return Task.FromResult(current);
    }

    private static bool SameProcess(AG2ProcessSnapshot left, AG2ProcessSnapshot right) =>
        left.ProcessId == right.ProcessId && SameStart(left.StartTimeUtc, right.StartTimeUtc) &&
        PathEquals(left.ExecutablePath, right.ExecutablePath) &&
        left.ExecutableLength == right.ExecutableLength &&
        left.ExecutableLastWriteUtc == right.ExecutableLastWriteUtc;

    private static bool SameStart(DateTime left, DateTime right) =>
        Math.Abs((left.ToUniversalTime() - right.ToUniversalTime()).TotalSeconds) <= 2;

    private static bool PathEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ParseWindowsCommandLine(string commandLine, string executablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native Antigravity process transition requires Windows.");
        }

        IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv == IntPtr.Zero || argc < 1)
        {
            throw new AG2ProcessLifecycleException("Windows could not parse the captured process command line.");
        }

        try
        {
            var tokens = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                IntPtr item = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                tokens[i] = Marshal.PtrToStringUni(item) ?? string.Empty;
            }

            int start = PathEquals(tokens[0], executablePath) ? 1 : 0;
            return tokens.Skip(start).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static IReadOnlyList<string> SanitizeArguments(IReadOnlyList<string> arguments)
    {
        string[] sensitive =
        [
            "--csrf_token", "--host_bridge_token", "--token", "--auth_token",
            "--access_token", "--session_token", "--password"
        ];
        var result = new List<string>(arguments.Count);
        bool redactNext = false;
        foreach (string argument in arguments)
        {
            if (redactNext)
            {
                result.Add("[REDACTED]");
                redactNext = false;
                continue;
            }
            string? matched = sensitive.FirstOrDefault(flag =>
                argument.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                result.Add(argument[..(matched.Length + 1)] + "[REDACTED]");
                continue;
            }
            result.Add(AG2Security.SanitizeCommandLine(argument));
            if (sensitive.Contains(argument, StringComparer.OrdinalIgnoreCase)) redactNext = true;
        }
        return result;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
