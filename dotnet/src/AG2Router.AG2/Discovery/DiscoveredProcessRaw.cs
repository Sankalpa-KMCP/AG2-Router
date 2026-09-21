namespace AG2Router.AG2.Discovery;

/// <summary>
/// Raw process metadata captured during operating system enumeration.
/// </summary>
public record DiscoveredProcessRaw(
    int ProcessId,
    string Name,
    string CommandLine,
    string ExecutablePath,
    DateTime? StartTime = null
);
