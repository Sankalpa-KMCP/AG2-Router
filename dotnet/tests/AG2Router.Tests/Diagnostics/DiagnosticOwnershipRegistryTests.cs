using System.IO;
using AG2Router.Core.Diagnostics;
using Xunit;

namespace AG2Router.Tests.Diagnostics;

[Collection("DiagnosticSession")]
public sealed class DiagnosticOwnershipRegistryTests : IDisposable
{
    private readonly string _tempDir;

    public DiagnosticOwnershipRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AG2_Registry_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup in test temp
        }
    }

    [Fact]
    public void RegisterAndRetrieve_RoundTripsInMemoryAndPersistsToDisk()
    {
        var registry = new DiagnosticOwnershipRegistry(_tempDir);
        Assert.False(registry.IsCorrupted);
        Assert.Equal(0, registry.Count);

        var record = new OwnedSegmentRecord
        {
            SegmentId = "diag_20261011_120000_0001",
            FileName = "diag_20261011_120000_0001.jsonl",
            RunId = "run_12345",
            CreatedAtUtc = "2026-10-11T12:00:00.0000000Z",
            VolumeSerialNumber = 1234,
            FileIndexHigh = 5678,
            FileIndexLow = 9012,
            SizeBytes = 1024,
            Status = OwnedSegmentStatus.Active
        };

        registry.RegisterSegment(record);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetSegment("diag_20261011_120000_0001", out var retrieved));
        Assert.NotNull(retrieved);
        Assert.Equal(record.SegmentId, retrieved!.SegmentId);
        Assert.Equal(record.FileName, retrieved.FileName);
        Assert.Equal(record.RunId, retrieved.RunId);
        Assert.Equal(record.Status, retrieved.Status);

        // Reload from disk into a fresh registry instance
        var reloaded = new DiagnosticOwnershipRegistry(_tempDir);
        Assert.False(reloaded.IsCorrupted);
        Assert.Equal(1, reloaded.Count);
        Assert.True(reloaded.TryGetSegment("diag_20261011_120000_0001", out var reloadedRecord));
        Assert.NotNull(reloadedRecord);
        Assert.Equal(record.SegmentId, reloadedRecord!.SegmentId);
        Assert.Equal(record.VolumeSerialNumber, reloadedRecord.VolumeSerialNumber);
    }

    [Fact]
    public void UpdateSegmentStatus_ModifiesStateAndPersists()
    {
        var registry = new DiagnosticOwnershipRegistry(_tempDir);
        var record = new OwnedSegmentRecord
        {
            SegmentId = "diag_20261011_120000_0002",
            FileName = "diag_20261011_120000_0002.jsonl",
            RunId = "run_12345",
            CreatedAtUtc = "2026-10-11T12:00:00.0000000Z",
            VolumeSerialNumber = 1234,
            FileIndexHigh = 5678,
            FileIndexLow = 9012,
            SizeBytes = 1024,
            Status = OwnedSegmentStatus.Active
        };

        registry.RegisterSegment(record);
        registry.UpdateSegmentStatus("diag_20261011_120000_0002", OwnedSegmentStatus.Closed, 4096);

        Assert.True(registry.TryGetSegment("diag_20261011_120000_0002", out var updated));
        Assert.NotNull(updated);
        Assert.Equal(OwnedSegmentStatus.Closed, updated!.Status);
        Assert.Equal(4096, updated.SizeBytes);

        var reloaded = new DiagnosticOwnershipRegistry(_tempDir);
        Assert.True(reloaded.TryGetSegment("diag_20261011_120000_0002", out var reloadedRecord));
        Assert.Equal(OwnedSegmentStatus.Closed, reloadedRecord!.Status);
        Assert.Equal(4096, reloadedRecord.SizeBytes);
    }

    [Fact]
    public void RemoveSegment_RemovesFromRegistryAndPersists()
    {
        var registry = new DiagnosticOwnershipRegistry(_tempDir);
        var record = new OwnedSegmentRecord
        {
            SegmentId = "diag_20261011_120000_0003",
            FileName = "diag_20261011_120000_0003.jsonl",
            RunId = "run_12345",
            CreatedAtUtc = "2026-10-11T12:00:00.0000000Z",
            VolumeSerialNumber = 1234,
            FileIndexHigh = 5678,
            FileIndexLow = 9012,
            SizeBytes = 1024,
            Status = OwnedSegmentStatus.Active
        };

        registry.RegisterSegment(record);
        Assert.Equal(1, registry.Count);

        registry.RemoveSegment("diag_20261011_120000_0003");
        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryGetSegment("diag_20261011_120000_0003", out _));

        var reloaded = new DiagnosticOwnershipRegistry(_tempDir);
        Assert.Equal(0, reloaded.Count);
    }

    [Fact]
    public void CorruptedManifest_FailsClosed_WithoutOverwritingCorruptFile()
    {
        string registryPath = Path.Combine(_tempDir, DiagnosticOwnershipRegistry.RegistryFileName);
        const string corruptContent = "{\"invalid_json\": true, incomplete_trailing_data...";
        File.WriteAllText(registryPath, corruptContent);

        var registry = new DiagnosticOwnershipRegistry(_tempDir);
        Assert.True(registry.IsCorrupted);
        Assert.Equal(0, registry.Count);

        // Attempting to register a new segment must fail closed and NOT overwrite the corrupt file
        var record = new OwnedSegmentRecord
        {
            SegmentId = "diag_20261011_120000_0004",
            FileName = "diag_20261011_120000_0004.jsonl",
            RunId = "run_12345",
            CreatedAtUtc = "2026-10-11T12:00:00.0000000Z",
            VolumeSerialNumber = 1234,
            FileIndexHigh = 5678,
            FileIndexLow = 9012,
            SizeBytes = 1024,
            Status = OwnedSegmentStatus.Active
        };

        registry.RegisterSegment(record);

        // Verify the original corrupt bytes are preserved intact on disk
        Assert.Equal(corruptContent, File.ReadAllText(registryPath));
    }

    [Fact]
    public void UnrecognizedSchema_FailsClosed()
    {
        string registryPath = Path.Combine(_tempDir, DiagnosticOwnershipRegistry.RegistryFileName);
        const string alienSchemaContent = "{\"schemaVersion\": 999, \"namespace\": \"alien\", \"segments\": {}}";
        File.WriteAllText(registryPath, alienSchemaContent);

        var registry = new DiagnosticOwnershipRegistry(_tempDir);
        Assert.True(registry.IsCorrupted);
        Assert.Equal(0, registry.Count);
    }
}
