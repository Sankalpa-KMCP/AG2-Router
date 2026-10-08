using System.IO;
using System.Text;
using AG2Router.AG2.Switching;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Unit tests for the exact canonical journal companion guard primitive conforming to PROMPT #036 PART 26.
/// Verifies kernel-level sharing exclusion, exact-entry verification, and fail-closed error handling.
/// </summary>
public sealed class ExactJournalGuardTests : IDisposable
{
    private readonly string _tempDir;

    public ExactJournalGuardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AG2_Guard_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private string CreateJournalPath() =>
        Path.Combine(_tempDir, $"switch-journal-{Guid.NewGuid():N}.json");

    private static SwitchJournalEntry CreateSampleEntry(
        SwitchJournalState state = SwitchJournalState.RECORDED,
        string? transactionId = null) =>
        new()
        {
            TransactionId = transactionId ?? Guid.NewGuid().ToString("D"),
            State = state,
            SourceAccountId = "acc-source",
            TargetAccountId = "acc-target",
            UpdatedAt = new DateTimeOffset(2026, 10, 7, 8, 30, 0, TimeSpan.Zero),
            TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED
        };

    [Fact]
    public async Task Case01_ExactCanonicalPresent_GuardAcquired_EntryExposed()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);
        var entry = CreateSampleEntry();
        await store.WriteEntryAsync(entry);

        var result = await store.AcquireExactJournalGuardAsync(entry);

        Assert.Equal(ExactJournalGuardStatus.Acquired, result.Status);
        Assert.NotNull(result.Guard);
        using (result.Guard)
        {
            Assert.True(SwitchJournalStore.EntriesMatch(entry, result.Guard.GuardedEntry));
        }
    }

    [Fact]
    public async Task Case02_CanonicalAbsent_FailsClosed_Absent()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);
        var entry = CreateSampleEntry();

        var result = await store.AcquireExactJournalGuardAsync(entry);

        Assert.Equal(ExactJournalGuardStatus.Absent, result.Status);
        Assert.Null(result.Guard);
    }

    [Fact]
    public async Task Case03_CanonicalCorrupt_FailsClosed_Corrupt()
    {
        string path = CreateJournalPath();
        await File.WriteAllTextAsync(path, "{ corrupt json");
        var store = new SwitchJournalStore(path);
        var entry = CreateSampleEntry();

        var result = await store.AcquireExactJournalGuardAsync(entry);

        Assert.Equal(ExactJournalGuardStatus.Corrupt, result.Status);
        Assert.Null(result.Guard);
    }

    [Fact]
    public async Task Case04_CanonicalForeignOrMismatched_FailsClosed_NotMatched()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);
        var onDiskEntry = CreateSampleEntry(transactionId: Guid.NewGuid().ToString("D"));
        await store.WriteEntryAsync(onDiskEntry);

        var differentEntry = CreateSampleEntry(transactionId: Guid.NewGuid().ToString("D"));

        var result = await store.AcquireExactJournalGuardAsync(differentEntry);

        Assert.Equal(ExactJournalGuardStatus.NotMatched, result.Status);
        Assert.Null(result.Guard);
    }

    [Fact]
    public async Task Case05_CanonicalModified_ComparisonAgainstExpectedFailsClosed()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);
        var entry = CreateSampleEntry();
        await store.WriteEntryAsync(entry);

        // Before acquisition hook modifies the file on disk to a different state
        store.BeforeGuardAcquireHookAsync = async () =>
        {
            var modified = entry with { State = SwitchJournalState.QUARANTINED };
            await new SwitchJournalStore(path).WriteEntryAsync(modified);
        };

        var result = await store.AcquireExactJournalGuardAsync(entry);

        Assert.Equal(ExactJournalGuardStatus.NotMatched, result.Status);
        Assert.Null(result.Guard);
    }

    [Fact]
    public async Task Case06_GuardDeniesNonCooperatingDelete_WhileHeld()
    {
        if (!OperatingSystem.IsWindows()) return;

        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);
        var entry = CreateSampleEntry();
        await store.WriteEntryAsync(entry);

        var result = await store.AcquireExactJournalGuardAsync(entry);
        Assert.Equal(ExactJournalGuardStatus.Acquired, result.Status);
        Assert.NotNull(result.Guard);

        using (result.Guard)
        {
            // Attempting to delete the guarded file while the guard is held must fail with IOException (sharing violation)
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.True(File.Exists(path));
        }
    }

    [Fact]
    public async Task Case07_GuardDeniesNonCooperatingRename_WhileHeld()
    {
        if (!OperatingSystem.IsWindows()) return;

        string path = CreateJournalPath();
        string otherPath = path + ".renamed";
        var store = new SwitchJournalStore(path);
        var entry = CreateSampleEntry();
        await store.WriteEntryAsync(entry);

        var result = await store.AcquireExactJournalGuardAsync(entry);
        Assert.Equal(ExactJournalGuardStatus.Acquired, result.Status);
        Assert.NotNull(result.Guard);

        using (result.Guard)
        {
            // Attempting to rename/move the guarded file while the guard is held must fail with IOException
            Assert.Throws<IOException>(() => File.Move(path, otherPath));
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(otherPath));
        }
    }

    [Fact]
    public async Task Case08_GuardDeniesNonCooperatingOverwrite_WhileHeld()
    {
        if (!OperatingSystem.IsWindows()) return;

        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);
        var entry = CreateSampleEntry();
        await store.WriteEntryAsync(entry);

        var result = await store.AcquireExactJournalGuardAsync(entry);
        Assert.Equal(ExactJournalGuardStatus.Acquired, result.Status);
        Assert.NotNull(result.Guard);

        using (result.Guard)
        {
            // Opening for write without sharing permissions must fail with IOException
            Assert.Throws<IOException>(() =>
                new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        }
    }

    [Fact]
    public async Task Case09_GuardReleaseRestoresNormalOperations()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);
        var entry = CreateSampleEntry();
        await store.WriteEntryAsync(entry);

        var result = await store.AcquireExactJournalGuardAsync(entry);
        Assert.Equal(ExactJournalGuardStatus.Acquired, result.Status);
        Assert.NotNull(result.Guard);

        // Explicitly dispose guard
        result.Guard.Dispose();

        // Normal delete succeeds now
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Case10_UnsupportedPlatform_FailsClosed()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path)
        {
            IsWindowsPlatform = () => false
        };
        var entry = CreateSampleEntry();

        var result = await store.AcquireExactJournalGuardAsync(entry);

        Assert.Equal(ExactJournalGuardStatus.UnsupportedPlatform, result.Status);
        Assert.Null(result.Guard);
    }
}
