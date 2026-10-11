using System.IO;
using System.Text.Json;
using AG2Router.App.Diagnostics;
using AG2Router.Core.Diagnostics;
using AG2Router.Windows.Diagnostics;
using Xunit;

namespace AG2Router.Tests.Diagnostics;

[Collection("DiagnosticSession")]
public sealed class WindowsHandleFileDeletionTests : IDisposable
{
    private readonly string _tempDir;

    public WindowsHandleFileDeletionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AG2_HandleDel_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void TryDeleteOwnedFile_DeletesVerifiedFile_ThroughSameHandle()
    {
        string filePath = Path.Combine(_tempDir, "diag_20261010_120000_0001.jsonl");
        var header = new DiagnosticSegmentHeader
        {
            RecordType = "segmentHeader",
            Format = DiagnosticSegmentHeader.CurrentFormat,
            SchemaVersion = 1,
            SegmentId = "diag_20261010_120000_0001",
            RunId = DiagnosticRunContext.RunId,
            CreatedAtUtc = DateTime.UtcNow.ToString("o")
        };
        File.WriteAllText(filePath, header.ToJson() + "\n");
        Assert.True(File.Exists(filePath));

        bool result = WindowsHandleFileDeletion.TryDeleteOwnedFile(
            filePath,
            _tempDir,
            stream => BoundedJsonlDiagnosticSink.ValidateSegmentStreamOwnership(stream, Path.GetFileName(filePath)));

        Assert.True(result);
        Assert.False(File.Exists(filePath), "Verified file must be deleted committed via handle disposition.");
    }

    [Fact]
    public void TryDeleteOwnedFile_RejectsUnverifiedFile_LeavesFileUntouched()
    {
        string filePath = Path.Combine(_tempDir, "diag_20261010_120000_0002.jsonl");
        File.WriteAllText(filePath, "{\"recordType\":\"alienHeader\"}\n");
        Assert.True(File.Exists(filePath));

        bool result = WindowsHandleFileDeletion.TryDeleteOwnedFile(
            filePath,
            _tempDir,
            stream => BoundedJsonlDiagnosticSink.ValidateSegmentStreamOwnership(stream, Path.GetFileName(filePath)));

        Assert.False(result);
        Assert.True(File.Exists(filePath), "Unverified candidate must remain completely untouched on disk.");
    }

    [Fact]
    public void TryDeleteOwnedFile_RejectsDirectoryEscape_LeavesTargetUntouched()
    {
        string subDir = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(subDir);

        string outsideFile = Path.Combine(_tempDir, "outside.txt");
        File.WriteAllText(outsideFile, "outside content");

        // Attempt deletion of outsideFile claiming subDir as base directory
        bool result = WindowsHandleFileDeletion.TryDeleteOwnedFile(
            outsideFile,
            subDir,
            _ => true);

        Assert.False(result);
        Assert.True(File.Exists(outsideFile), "File outside designated base directory must never be deleted.");
    }

    [Fact]
    public void TryDeleteOwnedFile_HandlesLockedFile_ReturnsFalseWithoutThrowing()
    {
        string filePath = Path.Combine(_tempDir, "diag_20261010_120000_0003.jsonl");
        File.WriteAllText(filePath, "test content");

        // Open with FileShare.None to simulate exclusive lock by external process
        using (var lockStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool result = WindowsHandleFileDeletion.TryDeleteOwnedFile(
                filePath,
                _tempDir,
                _ => true);

            Assert.False(result);
        }

        Assert.True(File.Exists(filePath));
    }
}
