namespace AG2Router.AG2.Discovery;

/// <summary>
/// In-memory representation of an active, verified Antigravity 2 session.
/// Retained strictly in volatile memory; never written to disk or logs.
/// </summary>
public record CachedAG2Session(
    int Pid,
    int Port,
    string Protocol,
    string CsrfToken,
    string? BinaryPath,
    string? SanitizedCommandLine,
    string DiscoveredAt,
    DateTime? StartTime = null,
    long Generation = 0
);
