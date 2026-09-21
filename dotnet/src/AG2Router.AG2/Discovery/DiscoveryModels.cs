namespace AG2Router.AG2.Discovery;

public record AG2ProcessInfo(
    int Pid,
    int Port,
    string Protocol,
    string CsrfToken,
    string? CommandLine,
    string? BinaryPath,
    string DiscoveredAt
);

public record AG2DiscoveryResult(
    bool IsRunning,
    string Status,
    AG2ProcessInfo? ProcessInfo,
    string Message
);
