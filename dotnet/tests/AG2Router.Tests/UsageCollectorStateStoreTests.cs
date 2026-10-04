using System.IO;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Durable collector checkpoint store: roundtrip, fail-closed corruption/unsupported-version
/// handling, and internal-account-ids-only persistence.
/// </summary>
public class UsageCollectorStateStoreTests
{
    private sealed class TestTempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ag2_ucstate_{Guid.NewGuid():N}");

        public TestTempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsCheckpoint()
    {
        using var temp = new TestTempDirectory();
        var ledgerDir = System.IO.Path.Combine(temp.Path, "usage");
        var store = new UsageCollectorStateStore(ledgerDir);
        var completedAt = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        await store.SaveCheckpointAsync(completedAt, ["acc_b", "acc_a"]);

        var checkpoint = await store.GetCheckpointAsync();
        Assert.Equal(completedAt, checkpoint.LastCollectionCompletedAtUtc);
        Assert.Equal(new[] { "acc_a", "acc_b" }, checkpoint.VerifiedAccountIds);
    }

    [Fact]
    public async Task AbsentState_ReturnsEmptyCheckpoint()
    {
        using var temp = new TestTempDirectory();
        var store = new UsageCollectorStateStore(System.IO.Path.Combine(temp.Path, "usage"));

        var checkpoint = await store.GetCheckpointAsync();

        Assert.Null(checkpoint.LastCollectionCompletedAtUtc);
        Assert.Empty(checkpoint.VerifiedAccountIds);
    }

    [Fact]
    public async Task CorruptState_FailsClosed_AndPreservesBytes()
    {
        using var temp = new TestTempDirectory();
        var ledgerDir = System.IO.Path.Combine(temp.Path, "usage");
        Directory.CreateDirectory(ledgerDir);
        string statePath = System.IO.Path.Combine(ledgerDir, "usage-collector-state.json");
        const string corrupt = "{ not a checkpoint";
        await File.WriteAllTextAsync(statePath, corrupt);
        var store = new UsageCollectorStateStore(ledgerDir);

        await Assert.ThrowsAsync<UsageCollectorStateException>(() => store.GetCheckpointAsync());
        Assert.Equal(corrupt, await File.ReadAllTextAsync(statePath));
    }

    [Fact]
    public async Task UnsupportedVersion_FailsClosed()
    {
        using var temp = new TestTempDirectory();
        var ledgerDir = System.IO.Path.Combine(temp.Path, "usage");
        Directory.CreateDirectory(ledgerDir);
        string statePath = System.IO.Path.Combine(ledgerDir, "usage-collector-state.json");
        await File.WriteAllTextAsync(statePath,
            "{\"schemaVersion\":9,\"updatedAtUtc\":\"2026-10-04T12:00:00+00:00\",\"verifiedAccountIds\":[]}");
        var store = new UsageCollectorStateStore(ledgerDir);

        await Assert.ThrowsAsync<UsageCollectorStateException>(() => store.GetCheckpointAsync());
        Assert.Equal(9, JsonDocument.Parse(await File.ReadAllTextAsync(statePath)).RootElement.GetProperty("schemaVersion").GetInt32());
    }
}
