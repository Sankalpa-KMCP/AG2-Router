using System.Collections.Concurrent;

namespace AG2Router.AG2.Persistence;

/// <summary>
/// Process-lifetime fail-closed admission for a path with an unresolved mutation.
/// The original task retains its normal locks until it settles; quarantine prevents
/// another caller from waiting indefinitely or treating a late result as safe.
/// Clearing requires process restart and authoritative operator reconciliation.
/// </summary>
internal sealed class RecoveryQuarantine
{
    private int _marked;
    public bool IsMarked => Volatile.Read(ref _marked) != 0;
    public void Mark() => Interlocked.Exchange(ref _marked, 1);
}

internal static class RecoveryQuarantineRegistry
{
    private static readonly ConcurrentDictionary<string, RecoveryQuarantine> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    public static RecoveryQuarantine Get(string path) =>
        Entries.GetOrAdd(Path.GetFullPath(path), static _ => new RecoveryQuarantine());
}
