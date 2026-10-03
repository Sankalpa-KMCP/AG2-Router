using System.IO;
using AG2Router.AG2.Persistence;
using Xunit;

namespace AG2Router.Tests;

/// <summary>Disposes a real synthetic file lease, then optionally injects an I/O cleanup failure.</summary>
internal sealed class LeaseCleanupTestLease(IAsyncDisposable inner, string resource, bool fail) : IAsyncDisposable
{
    public int DisposeCount { get; private set; }
    public IOException Failure { get; } = new("Injected lease disposal failure.");

    public async ValueTask DisposeAsync()
    {
        DisposeCount++;
        Assert.Equal(1, DisposeCount);
        Assert.Equal(0, PathLockRegistry.Get(resource).CurrentCount);
        await inner.DisposeAsync();
        if (fail) throw Failure;
    }

    public static async Task AssertReacquirableAsync(string resource)
    {
        var gate = PathLockRegistry.Get(resource);
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(1)), "Same-path lock was stranded by cleanup.");
        try
        {
            Assert.Equal(0, gate.CurrentCount);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await using var lease = await CrossProcessFileLease.AcquireAsync(resource, deadline.Token);
        }
        finally { gate.Release(); }
        Assert.Equal(1, gate.CurrentCount);
    }
}
