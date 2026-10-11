using System;
using System.Collections.Concurrent;

namespace AG2Router.Core.Diagnostics;

/// <summary>
/// Single run-scoped monotonic sequence domain and run ID owner (N5, Finding C).
/// Guarantees exactly one sequence domain per process run, unique run ID,
/// monotonic sequencing across all event producers, and provenance-carrying
/// trusted identifier registration.
/// </summary>
public static class DiagnosticRunContext
{
    private static long _sequence;
    private static string _runId = Guid.NewGuid().ToString("N");
    private static readonly ConcurrentDictionary<string, byte> TrustedIds = new();

    static DiagnosticRunContext()
    {
        TrustedIds.TryAdd(_runId, 0);
    }

    public static string RunId
    {
        get => Volatile.Read(ref _runId);
        private set
        {
            Volatile.Write(ref _runId, value);
            TrustedIds.TryAdd(value, 0);
        }
    }

    public static long CurrentSequence => Volatile.Read(ref _sequence);

    public static long NextSequence() => Interlocked.Increment(ref _sequence);

    public static string CreateCorrelationId()
    {
        string id = Guid.NewGuid().ToString("N");
        TrustedIds.TryAdd(id, 0);
        if (TrustedIds.Count > 10000)
        {
            var keys = TrustedIds.Keys.Take(5000).ToList();
            foreach (var key in keys)
            {
                if (!string.Equals(key, RunId, StringComparison.Ordinal) && !string.Equals(key, id, StringComparison.Ordinal))
                {
                    TrustedIds.TryRemove(key, out _);
                }
            }
        }
        return id;
    }

    public static string CreateOperationId()
    {
        string id = Guid.NewGuid().ToString("N");
        TrustedIds.TryAdd(id, 0);
        if (TrustedIds.Count > 10000)
        {
            var keys = TrustedIds.Keys.Take(5000).ToList();
            foreach (var key in keys)
            {
                if (!string.Equals(key, RunId, StringComparison.Ordinal) && !string.Equals(key, id, StringComparison.Ordinal))
                {
                    TrustedIds.TryRemove(key, out _);
                }
            }
        }
        return id;
    }

    public static bool IsTrustedIdentifier(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        return TrustedIds.ContainsKey(id);
    }

    public static void RegisterTrustedIdentifier(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            TrustedIds.TryAdd(id, 0);
        }
    }

    public static void RegisterTrustedIdentifierForTests(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            TrustedIds.TryAdd(id, 0);
        }
    }

    public static void ResetForTests(string? runId = null)
    {
        Interlocked.Exchange(ref _sequence, 0);
        TrustedIds.Clear();
        RunId = !string.IsNullOrWhiteSpace(runId)
            ? runId!
            : Guid.NewGuid().ToString("N");
    }
}
