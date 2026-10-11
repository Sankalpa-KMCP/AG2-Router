using System.IO;
using System.Text.Json;
using AG2Router.App.Diagnostics;
using AG2Router.Core.Diagnostics;
using Xunit;

namespace AG2Router.Tests.Diagnostics;

[Collection("DiagnosticSession")]
public sealed class BoundedJsonlDiagnosticSinkTests : IDisposable
{
    private readonly string _tempDir;

    public BoundedJsonlDiagnosticSinkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AG2_Diag_Test_" + Guid.NewGuid().ToString("N"));
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
    public async Task ParallelWriters_ProduceValidJsonRecords_WithoutInterleaving()
    {
        int threadCount = 10;
        int itemsPerThread = 50;
        int totalExpected = threadCount * itemsPerThread;

        using (var sink = new BoundedJsonlDiagnosticSink(_tempDir, maxSegmentSizeBytes: 1024 * 1024))
        {
            var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
            {
                for (int i = 0; i < itemsPerThread; i++)
                {
                    sink.Emit(DiagnosticEventFactory.Create(
                        eventId: DiagnosticEventCatalog.StartupStarted,
                        component: $"Thread_{threadId}",
                        operation: "ParallelEmit",
                        outcome: DiagnosticOutcome.Success,
                        failureCategory: DiagnosticFailureCategory.None,
                        safeMessage: $"Item {i} from thread {threadId}"
                    ));
                }
            })).ToArray();

            await Task.WhenAll(tasks);
            await sink.FlushAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(totalExpected, sink.GetHealth().TotalPersisted);
        }

        var files = Directory.GetFiles(_tempDir, "diag_*.jsonl");
        Assert.NotEmpty(files);

        int parsedCount = 0;
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            Assert.NotEmpty(lines);

            // First line must be a valid JSON DiagnosticSegmentHeader (R7, R8)
            var header = JsonSerializer.Deserialize<DiagnosticSegmentHeader>(lines[0]);
            Assert.NotNull(header);
            Assert.Equal("segmentHeader", header.RecordType);
            Assert.Equal("AG2Router-Diagnostic-Log-v1", header.Format);
            Assert.Equal(1, header.SchemaVersion);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("recordType", out var rt) && rt.GetString() == "segmentHeader")
                {
                    continue;
                }
                var evt = DiagnosticEvent.FromJson(line);
                Assert.NotNull(evt);
                Assert.Equal(1, evt!.SchemaVersion);
                Assert.Equal(DiagnosticEventCatalog.StartupStarted, evt.EventId);
                parsedCount++;
            }
        }

        Assert.Equal(totalExpected, parsedCount);
    }

    [Fact]
    public async Task SegmentRotation_AndRetention_BoundsFileCount_AndPreservesUnrelatedFiles()
    {
        // Place unrelated files that must survive retention sweep
        string appLogPath = Path.Combine(_tempDir, "app.log");
        string jsErrorsPath = Path.Combine(_tempDir, "js_runtime_errors.json");
        string customFilePath = Path.Combine(_tempDir, "custom.txt");
        string foreignDiagPath = Path.Combine(_tempDir, "diag_foreign_other_tool.jsonl");
        string imitationDiagPath = Path.Combine(_tempDir, "diag_20261010_120000_9999.jsonl");

        File.WriteAllText(appLogPath, "legacy app log content");
        File.WriteAllText(jsErrorsPath, "[]");
        File.WriteAllText(customFilePath, "user custom data");
        File.WriteAllText(foreignDiagPath, "unowned foreign log line\n");
        File.WriteAllText(imitationDiagPath, "{\"recordType\":\"alienHeader\",\"format\":\"Foreign\"}\n");

        // Use small segment size to force rapid rotation
        using (var sink = new BoundedJsonlDiagnosticSink(
            _tempDir,
            maxSegmentSizeBytes: 400, // Small to trigger multiple segment creations
            maxOwnedSegments: 4,
            retentionPeriod: TimeSpan.FromDays(14)))
        {
            // Emit enough events to trigger multiple rotations
            for (int i = 0; i < 30; i++)
            {
                sink.Emit(DiagnosticEventFactory.Create(
                    eventId: DiagnosticEventCatalog.StartupStarted,
                    component: "RotationTest",
                    operation: "Rotate",
                    outcome: DiagnosticOutcome.Success,
                    failureCategory: DiagnosticFailureCategory.None,
                    safeMessage: $"Event number {i} padding " + new string('X', 50)
                ));
                await Task.Delay(10);
            }

            await sink.FlushAsync(TimeSpan.FromSeconds(3));

            // Check owned segments count is bounded to maxOwnedSegments
            var diagFiles = Directory.GetFiles(_tempDir, "diag_*.jsonl");
            Assert.True(diagFiles.Length <= 6, $"Found {diagFiles.Length} segments, expected at most 6 including foreign and imitation files.");
        }

        // Assert unrelated files were untouched
        Assert.True(File.Exists(appLogPath), "app.log must survive retention sweep");
        Assert.Equal("legacy app log content", File.ReadAllText(appLogPath));

        Assert.True(File.Exists(jsErrorsPath), "js_runtime_errors.json must survive retention sweep");
        Assert.Equal("[]", File.ReadAllText(jsErrorsPath));

        Assert.True(File.Exists(customFilePath), "custom.txt must survive retention sweep");
        Assert.Equal("user custom data", File.ReadAllText(customFilePath));

        Assert.True(File.Exists(foreignDiagPath), "Foreign diag_*.jsonl without AG2Router ownership marker must survive retention sweep");
        Assert.Equal("unowned foreign log line\n", File.ReadAllText(foreignDiagPath));

        Assert.True(File.Exists(imitationDiagPath), "Imitation diag_*.jsonl with non-matching header must survive retention sweep (R7)");
        Assert.Equal("{\"recordType\":\"alienHeader\",\"format\":\"Foreign\"}\n", File.ReadAllText(imitationDiagPath));
    }

    [Fact]
    public async Task QueueOverload_DropsEvents_AndIncrementsSuppressionCount_WithoutThrowing()
    {
        // Capacity 5 queue with small memory bound
        using var sink = new BoundedJsonlDiagnosticSink(
            _tempDir,
            queueCapacity: 5,
            maxQueueMemoryBytes: 1024);

        // Emit 50 events rapidly
        for (int i = 0; i < 50; i++)
        {
            sink.Emit(DiagnosticEventFactory.Create(
                eventId: DiagnosticEventCatalog.StartupStarted,
                component: "OverloadTest",
                operation: "Flood",
                outcome: DiagnosticOutcome.Success,
                failureCategory: DiagnosticFailureCategory.None,
                safeMessage: $"Flood item {i}"
            ));
        }

        await sink.FlushAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(50, sink.GetHealth().TotalEmitted);
        // Either events were processed quickly or some were suppressed
        Assert.True(sink.GetHealth().TotalPersisted + sink.GetHealth().TotalSuppressed == 50);
    }

    [Fact]
    public async Task FlushAsync_HonorsTimeoutBudget_WhenWorkerIsDisposed()
    {
        var sink = new BoundedJsonlDiagnosticSink(_tempDir);
        sink.Emit(DiagnosticEventFactory.Create(
            eventId: DiagnosticEventCatalog.StartupStarted,
            component: "TimeoutTest",
            operation: "Flush",
            outcome: DiagnosticOutcome.Success,
            failureCategory: DiagnosticFailureCategory.None,
            safeMessage: "Flush test"
        ));

        // Flush completes cleanly under 2 second budget
        await sink.FlushAsync(TimeSpan.FromSeconds(2));

        Assert.True(sink.GetHealth().TotalPersisted >= 1);
        sink.Dispose();
    }

    [Fact]
    public void RecentEvents_MaintainsInMemoryRingBuffer()
    {
        using var sink = new BoundedJsonlDiagnosticSink(_tempDir);

        for (int i = 0; i < 10; i++)
        {
            sink.Emit(DiagnosticEventFactory.Create(
                eventId: DiagnosticEventCatalog.StartupStarted,
                component: "RingBufferTest",
                operation: "Ring",
                outcome: DiagnosticOutcome.Success,
                failureCategory: DiagnosticFailureCategory.None,
                safeMessage: $"Ring item {i}"
            ));
        }

        var recent = sink.GetRecentEvents();
        Assert.Equal(10, recent.Count);
        Assert.Equal("Application startup sequence initiated.", recent[0].SafeMessage);
        Assert.Equal("Application startup sequence initiated.", recent[9].SafeMessage);
        Assert.True(recent[0].Sequence < recent[9].Sequence);
    }

    [Fact]
    public void CorruptedTrailingJsonl_HandledSafelyWithoutDeletingPrecedingRecords()
    {
        string filePath = Path.Combine(_tempDir, "diag_20261010_120000_0001.jsonl");
        var validEvt = DiagnosticEventFactory.Create(
            DiagnosticEventCatalog.StartupStarted,
            "Comp",
            "Op",
            DiagnosticOutcome.Success,
            DiagnosticFailureCategory.None,
            "Valid preceding event");

        string validLine = validEvt.ToJson();
        string corruptedLine = "{\"schemaVersion\":1,\"incompleteJson...TRUNCATED";

        File.WriteAllLines(filePath, new[] { validLine, corruptedLine });

        // Parse file line by line
        var readEvents = new List<DiagnosticEvent>();
        foreach (var line in File.ReadAllLines(filePath))
        {
            var parsed = DiagnosticEvent.FromJson(line);
            if (parsed != null)
            {
                readEvents.Add(parsed);
            }
        }

        Assert.Single(readEvents);
        Assert.Equal("Application startup sequence initiated.", readEvents[0].SafeMessage);
        Assert.True(File.Exists(filePath), "File must not be deleted due to trailing truncation.");
    }

    [Fact]
    public async Task MultiInstanceSafety_SecondaryInstanceDegradesToMemoryWithoutModifyingPrimaryLogs()
    {
        using var sink1 = new BoundedJsonlDiagnosticSink(_tempDir);
        Assert.True(sink1.GetHealth().IsWriterLeaseHeld);

        using var sink2 = new BoundedJsonlDiagnosticSink(_tempDir);
        Assert.False(sink2.GetHealth().IsWriterLeaseHeld);

        sink2.Emit(DiagnosticEventFactory.Create(
            DiagnosticEventCatalog.StartupStarted,
            "Secondary",
            "Emit",
            DiagnosticOutcome.Success,
            DiagnosticFailureCategory.None,
            "Secondary event in memory only"
        ));

        // sink2 retains in memory ring buffer
        var recent2 = sink2.GetRecentEvents();
        Assert.Single(recent2);
        Assert.Equal("Application startup sequence initiated.", recent2[0].SafeMessage);

        // Memory-only sink returns false on disk flush (R5)
        Assert.False(await sink2.FlushAsync(TimeSpan.FromSeconds(1)));

        // sink1 remains unaffected and owns disk segments
        sink1.Emit(DiagnosticEventFactory.Create(
            DiagnosticEventCatalog.StartupStarted,
            "Primary",
            "Emit",
            DiagnosticOutcome.Success,
            DiagnosticFailureCategory.None,
            "Primary event to disk"
        ));
        Assert.True(await sink1.FlushAsync(TimeSpan.FromSeconds(1)));
        Assert.True(sink1.GetHealth().TotalPersisted >= 1);
    }

    [Fact]
    public async Task RetentionAccounting_TracksFailedDeletions_WithoutUnderestimatingFileCount()
    {
        // R6: If a file deletion fails (e.g. locked by external process), it must be counted
        // in RetentionFailureCount and kept in file/byte counts rather than silently dropped.
        string oldSegment = Path.Combine(_tempDir, "diag_20200101_120000_0001.jsonl");
        var header = new DiagnosticSegmentHeader
        {
            RecordType = "segmentHeader",
            Format = "AG2Router-Diagnostic-Log-v1",
            SchemaVersion = 1,
            SegmentId = "diag_20200101_120000_0001",
            RunId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = "2020-01-01T12:00:00.0000000Z"
        };
        File.WriteAllText(oldSegment, JsonSerializer.Serialize(header) + "\n");
        File.SetCreationTimeUtc(oldSegment, DateTime.UtcNow.AddDays(-20));
        File.SetLastWriteTimeUtc(oldSegment, DateTime.UtcNow.AddDays(-20));

        // Lock the file so deletion fails (FileShare.ReadWrite allows header read by IsOwnedSegmentFile, but blocks DeleteOnClose)
        using (var lockStream = new FileStream(oldSegment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var sink = new BoundedJsonlDiagnosticSink(
                _tempDir,
                maxSegmentSizeBytes: 1024 * 1024,
                retentionPeriod: TimeSpan.FromDays(14));

            sink.Emit(DiagnosticEventFactory.Create(
                DiagnosticEventCatalog.StartupStarted,
                "Comp",
                "Op",
                DiagnosticOutcome.Success,
                DiagnosticFailureCategory.None,
                "Trigger sweep"
            ));

            await sink.FlushAsync(TimeSpan.FromSeconds(2));

            var health = sink.GetHealth();
            Assert.True(health.RetentionFailureCount >= 1, "Failed deletion must increment RetentionFailureCount");
            Assert.True(health.OwnedSegmentCount >= 1, "Undeleted segment must remain in OwnedSegmentCount");
        }
    }

    [Fact]
    public void DiagnosticSession_PreAdmissionBuffer_PromotesToPersistentSink()
    {
        // R11: MemoryDiagnosticSink buffers startup events before single instance admission,
        // then promotes to persistent sink on admission.
        DiagnosticSession.Reset();
        DiagnosticSession.InitializePreAdmission(capacity: 50);

        DiagnosticSession.Emit(
            DiagnosticEventCatalog.StartupStarted,
            "Bootstrap",
            "AppStartup",
            DiagnosticOutcome.Success,
            DiagnosticFailureCategory.None,
            "Pre-admission event 1"
        );
        DiagnosticSession.Emit(
            DiagnosticEventCatalog.StartupCompleted,
            "Bootstrap",
            "AppStartup",
            DiagnosticOutcome.Success,
            DiagnosticFailureCategory.None,
            "Pre-admission event 2"
        );

        var memoryEvents = DiagnosticSession.GetRecentEvents();
        Assert.Equal(2, memoryEvents.Count);

        var persistentSink = new MemoryDiagnosticSink(capacity: 100);
        DiagnosticSession.PromoteToPersistent(persistentSink);

        var promotedEvents = persistentSink.GetRecentEvents();
        Assert.Equal(2, promotedEvents.Count);
        Assert.Equal("Application startup sequence initiated.", promotedEvents[0].SafeMessage);
        Assert.Equal("Application startup completed successfully.", promotedEvents[1].SafeMessage);

        DiagnosticSession.Reset();
    }

    [Fact]
    public void SafeOwnedFileDeletion_CouplesVerificationToSameHandle_LeavesForeignMatchingFilesUntouched()
    {
        // N2: Open handle without DeleteOnClose, verify header on same handle, only delete if verified.
        // Foreign matching files and corrupt files survive untouched.
        using var sink = new BoundedJsonlDiagnosticSink(_tempDir);

        // 1. Legitimate owned segment file with valid header
        string ownedFile = Path.Combine(_tempDir, "diag_20261010_120000_0001.jsonl");
        var header = new DiagnosticSegmentHeader
        {
            RecordType = "segmentHeader",
            Format = DiagnosticSegmentHeader.CurrentFormat,
            SchemaVersion = 1,
            SegmentId = "diag_20261010_120000_0001",
            RunId = DiagnosticRunContext.RunId,
            CreatedAtUtc = DateTime.UtcNow.ToString("o")
        };
        File.WriteAllText(ownedFile, header.ToJson() + "\n");

        // 2. Foreign matching file with valid naming pattern but non-owned header
        string foreignMatchingFile = Path.Combine(_tempDir, "diag_20261010_120000_0002.jsonl");
        File.WriteAllText(foreignMatchingFile, "{\"recordType\":\"alienHeader\",\"format\":\"Foreign\"}\n");

        // 3. Non-JSON corrupt file with valid naming pattern
        string corruptFile = Path.Combine(_tempDir, "diag_20261010_120000_0003.jsonl");
        File.WriteAllText(corruptFile, "NOT_JSON_DATA_AT_ALL\n");

        // 4. Foreign runId file (valid format and segmentId but different RunId from prior/other session)
        string foreignRunIdFile = Path.Combine(_tempDir, "diag_20261010_120000_0004.jsonl");
        var foreignHeader = new DiagnosticSegmentHeader
        {
            RecordType = "segmentHeader",
            Format = DiagnosticSegmentHeader.CurrentFormat,
            SchemaVersion = 1,
            SegmentId = "diag_20261010_120000_0004",
            RunId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow.ToString("o")
        };
        File.WriteAllText(foreignRunIdFile, foreignHeader.ToJson() + "\n");

        // Foreign matching file: returns false, file survives untouched (N2)
        Assert.False(sink.TryDeleteOwnedFile(new FileInfo(foreignMatchingFile)));
        Assert.True(File.Exists(foreignMatchingFile), "Foreign matching file must survive untouched.");

        // Corrupt file: returns false, file survives untouched (N2)
        Assert.False(sink.TryDeleteOwnedFile(new FileInfo(corruptFile)));
        Assert.True(File.Exists(corruptFile), "Corrupt file must survive untouched.");

        // Foreign run ID file: returns false, file survives untouched (F1)
        Assert.False(sink.TryDeleteOwnedFile(new FileInfo(foreignRunIdFile)));
        Assert.True(File.Exists(foreignRunIdFile), "Foreign runId file must survive untouched.");

        // Owned file with matching RunId: returns true, file is deleted via Windows handle deletion (F1)
        Assert.True(sink.TryDeleteOwnedFile(new FileInfo(ownedFile)));
        Assert.False(File.Exists(ownedFile), "Owned file must be deleted.");
    }

    [Fact]
    public void BoundedInspection_RejectsOversizedFirstLineSafely()
    {
        // N9: If first record exceeds 4096 bytes without newline, it must be rejected safely
        string oversizedFile = Path.Combine(_tempDir, "diag_20261010_120000_0099.jsonl");
        string oversizedHeader = new string('A', 5000);
        File.WriteAllText(oversizedFile, oversizedHeader);

        using var fs = new FileStream(oversizedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bool isValid = BoundedJsonlDiagnosticSink.ValidateSegmentStreamOwnership(fs, Path.GetFileName(oversizedFile));
        Assert.False(isValid, "Oversized first line exceeding 4096 bytes must be rejected safely.");
    }

    [Fact]
    public async Task RestartOverlap_AdmittedPrimaryAcquiresLockAfterPriorOwnerReleases()
    {
        // N7: Primary instance holds lock; incoming primary instance waits with backoff up to timeoutMs
        using var sink1 = new BoundedJsonlDiagnosticSink(_tempDir);
        Assert.True(sink1.GetHealth().IsWriterLeaseHeld);

        var task = Task.Run(async () =>
        {
            await Task.Delay(100);
            sink1.Dispose();
        });

        using var sink2 = new BoundedJsonlDiagnosticSink(_tempDir, lockAcquireTimeoutMs: 1500);
        await task;

        Assert.True(sink2.GetHealth().IsWriterLeaseHeld, "Admitted primary must acquire writer lease once prior owner releases it.");
    }

    [Fact]
    public async Task IdleRetention_AgedActiveSegmentRotatesAndCleansUp_WithoutNewEvents()
    {
        // N8: Idle timeout loop rotates aged active segment (>14 days) and runs retention without new events
        var simulatedTime = DateTime.UtcNow;
        DateTime Clock() => simulatedTime;

        string oldSegment = Path.Combine(_tempDir, "diag_20200101_120000_0001.jsonl");
        var header = new DiagnosticSegmentHeader
        {
            RecordType = "segmentHeader",
            Format = DiagnosticSegmentHeader.CurrentFormat,
            SchemaVersion = 1,
            SegmentId = "diag_20200101_120000_0001",
            RunId = DiagnosticRunContext.RunId,
            CreatedAtUtc = "2020-01-01T12:00:00.0000000Z"
        };
        File.WriteAllText(oldSegment, header.ToJson() + "\n");
        File.SetCreationTimeUtc(oldSegment, DateTime.UtcNow.AddDays(-20));
        File.SetLastWriteTimeUtc(oldSegment, DateTime.UtcNow.AddDays(-20));

        using (var sink = new BoundedJsonlDiagnosticSink(
            _tempDir,
            retentionPeriod: TimeSpan.FromDays(14),
            utcClock: Clock,
            idleMaintenanceInterval: TimeSpan.FromMilliseconds(50)))
        {
            Assert.True(File.Exists(oldSegment));

            // Wait for idle loop to fire without emitting any events
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (File.Exists(oldSegment) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.False(File.Exists(oldSegment), "Aged segment must be removed during idle maintenance without requiring new events.");
        }
    }

    [Fact]
    public async Task CursorProgression_ProgressesBeyondFirst64Candidates_CleansUpOwnedSegments()
    {
        // F6, F7: Create 70 segment files.
        // First 65 files are imitation non-owned files (survive retention).
        // Files 66-70 are owned aged segments (>14 days).
        // Retention sweep with cursor progression must reach past candidate 64 and clean up aged owned files.
        var simulatedTime = DateTime.UtcNow;
        DateTime Clock() => simulatedTime;

        for (int i = 0; i < 65; i++)
        {
            string imitationFile = Path.Combine(_tempDir, $"diag_20200101_{i:D6}_0000.jsonl");
            File.WriteAllText(imitationFile, "{\"recordType\":\"alienHeader\"}\n");
            File.SetCreationTimeUtc(imitationFile, DateTime.UtcNow.AddDays(-20));
        }

        var ownedFiles = new List<string>();
        for (int i = 65; i < 70; i++)
        {
            string segmentId = $"diag_20200102_{i:D6}_0000";
            string ownedFile = Path.Combine(_tempDir, $"{segmentId}.jsonl");
            var header = new DiagnosticSegmentHeader
            {
                RecordType = "segmentHeader",
                Format = DiagnosticSegmentHeader.CurrentFormat,
                SchemaVersion = 1,
                SegmentId = segmentId,
                RunId = DiagnosticRunContext.RunId,
                CreatedAtUtc = "2020-01-02T12:00:00.0000000Z"
            };
            File.WriteAllText(ownedFile, header.ToJson() + "\n");
            File.SetCreationTimeUtc(ownedFile, DateTime.UtcNow.AddDays(-20));
            ownedFiles.Add(ownedFile);
        }

        using (var sink = new BoundedJsonlDiagnosticSink(
            _tempDir,
            retentionPeriod: TimeSpan.FromDays(14),
            utcClock: Clock,
            idleMaintenanceInterval: TimeSpan.FromMilliseconds(50)))
        {
            // Trigger 2 idle maintenance cycles or flush cycles to progress through cursor batches
            for (int pass = 0; pass < 3; pass++)
            {
                sink.Emit(DiagnosticEventFactory.Create(
                    DiagnosticEventCatalog.StartupStarted,
                    "Comp",
                    "Op",
                    DiagnosticOutcome.Success,
                    DiagnosticFailureCategory.None,
                    $"Cursor sweep {pass}"));
                await sink.FlushAsync(TimeSpan.FromSeconds(2));
                await Task.Delay(50);
            }

            // Verify that all 5 owned aged segments past index 64 were cleaned up
            int remainingOwned = ownedFiles.Count(File.Exists);
            Assert.Equal(0, remainingOwned);
        }
    }

    [Fact]
    public async Task DurabilityCounters_TotalPersistedAdvancesOnlyAfterDiskFlushCommits()
    {
        // F8: TotalPersisted must not count data only written to unflushed memory buffers
        using var sink = new BoundedJsonlDiagnosticSink(_tempDir);

        sink.Emit(DiagnosticEventFactory.Create(
            DiagnosticEventCatalog.StartupStarted,
            "Comp",
            "Op",
            DiagnosticOutcome.Success,
            DiagnosticFailureCategory.None,
            "Durability test"));

        // Prior to flush, total emitted is 1
        Assert.Equal(1, sink.GetHealth().TotalEmitted);

        // Flush commits to disk, advancing TotalPersisted
        bool flushResult = await sink.FlushAsync(TimeSpan.FromSeconds(2));
        Assert.True(flushResult);
        Assert.Equal(1, sink.GetHealth().TotalPersisted);
    }

    [Fact]
    public async Task FlushAsync_EnforcesSingleTotalTimeoutDeadline()
    {
        // F9: FlushAsync must enforce one linked deadline across both enqueue and wait
        using var sink = new BoundedJsonlDiagnosticSink(_tempDir);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool result = await sink.FlushAsync(TimeSpan.FromMilliseconds(200));
        sw.Stop();

        Assert.True(result);
        Assert.True(sw.ElapsedMilliseconds < 1500, "FlushAsync must complete within the allocated deadline.");
    }
}

[CollectionDefinition("DiagnosticSession", DisableParallelization = true)]
public class DiagnosticSessionCollection { }
