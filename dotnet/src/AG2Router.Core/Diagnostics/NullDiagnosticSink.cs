namespace AG2Router.Core.Diagnostics;

/// <summary>
/// Safe no-op implementation of <see cref="IDiagnosticSink"/> for testing and uninstrumented configurations.
/// </summary>
public sealed class NullDiagnosticSink : IDiagnosticSink
{
    public static readonly NullDiagnosticSink Instance = new();

    public void Emit(DiagnosticEvent @event) { }

    public Task<bool> FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public DiagnosticSinkHealth GetHealth() =>
        new(IsHealthy: true, TotalEmitted: 0, TotalPersisted: 0, TotalSuppressed: 0, TotalDropped: 0, LastError: null);

    public IReadOnlyList<DiagnosticEvent> GetRecentEvents() =>
        Array.Empty<DiagnosticEvent>();

    public bool IsEnabled(DiagnosticSeverity severity) => false;
}
