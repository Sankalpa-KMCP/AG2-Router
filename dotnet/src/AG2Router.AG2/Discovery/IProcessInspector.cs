namespace AG2Router.AG2.Discovery;

/// <summary>
/// Operating system abstraction for process enumeration, port discovery, and liveness checks.
/// Fully decoupled from native OS calls to enable deterministic in-memory unit testing.
/// </summary>
public interface IProcessInspector
{
    /// <summary>
    /// Enumerates candidate language server processes currently running.
    /// </summary>
    Task<IReadOnlyList<DiscoveredProcessRaw>> FindProcessesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all local loopback TCP ports (127.0.0.1 or ::1) on which the specified process is listening.
    /// </summary>
    Task<IReadOnlyList<int>> GetListeningPortsAsync(int processId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies if a process ID remains alive and active, optionally verifying process start time to guard against PID recycling.
    /// </summary>
    bool IsPidAlive(int processId, DateTime? expectedStartTime = null);
}
