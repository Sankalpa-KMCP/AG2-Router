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

    [Fact]
    public async Task SaveCheckpoint_WhenFileIsCorrupt_FailsClosedAndPreservesCorruptBytes()
    {
        using var temp = new TestTempDirectory();
        var ledgerDir = System.IO.Path.Combine(temp.Path, "usage");
        Directory.CreateDirectory(ledgerDir);
        string statePath = System.IO.Path.Combine(ledgerDir, "usage-collector-state.json");
        const string corrupt = "{ not a valid checkpoint";
        await File.WriteAllTextAsync(statePath, corrupt);
        var store = new UsageCollectorStateStore(ledgerDir);

        var ex = await Assert.ThrowsAsync<UsageCollectorStateException>(() =>
            store.SaveCheckpointAsync(DateTimeOffset.UtcNow, ["acc_a"]));

        Assert.Contains(statePath, ex.Message);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(statePath));
    }

    [Fact]
    public async Task SaveCheckpoint_WhenUnsupportedVersion_FailsClosedAndPreservesBytes()
    {
        using var temp = new TestTempDirectory();
        var ledgerDir = System.IO.Path.Combine(temp.Path, "usage");
        Directory.CreateDirectory(ledgerDir);
        string statePath = System.IO.Path.Combine(ledgerDir, "usage-collector-state.json");
        const string unsupported = "{\"schemaVersion\":99,\"updatedAtUtc\":\"2026-10-04T12:00:00+00:00\",\"verifiedAccountIds\":[]}";
        await File.WriteAllTextAsync(statePath, unsupported);
        var store = new UsageCollectorStateStore(ledgerDir);

        await Assert.ThrowsAsync<UsageCollectorStateException>(() =>
            store.SaveCheckpointAsync(DateTimeOffset.UtcNow, ["acc_a"]));

        Assert.Equal(unsupported, await File.ReadAllTextAsync(statePath));
    }

    [Fact]
    public async Task SaveCheckpoint_WhenEmptyOrTruncated_FailsClosedAndPreservesEmptyBytes()
    {
        using var temp = new TestTempDirectory();
        var ledgerDir = System.IO.Path.Combine(temp.Path, "usage");
        Directory.CreateDirectory(ledgerDir);
        string statePath = System.IO.Path.Combine(ledgerDir, "usage-collector-state.json");
        await File.WriteAllTextAsync(statePath, "   ");
        var store = new UsageCollectorStateStore(ledgerDir);

        await Assert.ThrowsAsync<UsageCollectorStateException>(() =>
            store.SaveCheckpointAsync(DateTimeOffset.UtcNow, ["acc_a"]));

        Assert.Equal("   ", await File.ReadAllTextAsync(statePath));
    }

    [Fact]
    public async Task SaveCheckpoint_WhenHealthy_SucceedsAndOverwrites()
    {
        using var temp = new TestTempDirectory();
        var ledgerDir = System.IO.Path.Combine(temp.Path, "usage");
        var store = new UsageCollectorStateStore(ledgerDir);

        var time1 = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        await store.SaveCheckpointAsync(time1, ["acc_1"]);
        var checkpoint1 = await store.GetCheckpointAsync();
        Assert.Equal(time1, checkpoint1.LastCollectionCompletedAtUtc);
        Assert.Equal(new[] { "acc_1" }, checkpoint1.VerifiedAccountIds);

        var time2 = new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero);
        await store.SaveCheckpointAsync(time2, ["acc_1", "acc_2"]);
        var checkpoint2 = await store.GetCheckpointAsync();
        Assert.Equal(time2, checkpoint2.LastCollectionCompletedAtUtc);
        Assert.Equal(new[] { "acc_1", "acc_2" }, checkpoint2.VerifiedAccountIds);
    }
}
