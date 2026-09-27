using System.IO;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Switching;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Pure persistence unit tests for SwitchJournalStore and Schema v1 conforming to ADR-001.
/// Covers all 18 persistence and classification scenarios using synthetic disposable fixtures.
/// </summary>
public sealed class SwitchJournalStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SwitchJournalStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AG2_Journal_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private string CreateJournalPath(string? fileName = null) =>
        Path.Combine(_tempDir, fileName ?? $"switch-journal-{Guid.NewGuid():N}.json");

    private static SwitchJournalEntry CreateSampleEntry(
        SwitchJournalState state = SwitchJournalState.RECORDED,
        string? transactionId = null,
        string sourceAccountId = "acc-source-123",
        string targetAccountId = "acc-target-456",
        string? quarantineReason = null,
        DateTimeOffset? updatedAt = null)
    {
        return new SwitchJournalEntry
        {
            TransactionId = transactionId ?? Guid.NewGuid().ToString("D"),
            State = state,
            SourceAccountId = sourceAccountId,
            TargetAccountId = targetAccountId,
            QuarantineReasonCode = quarantineReason,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow
        };
    }

    // 1. Schema v1 write/read round trip
    [Fact]
    public async Task Scenario01_SchemaV1_WriteRead_RoundTrip_Succeeds()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        var originalEntry = CreateSampleEntry(
            state: SwitchJournalState.CREDENTIAL_APPLYING,
            sourceAccountId: "acc-src-roundtrip",
            targetAccountId: "acc-tgt-roundtrip",
            quarantineReason: "TEST_REASON_CODE",
            updatedAt: new DateTimeOffset(2026, 9, 27, 12, 34, 56, TimeSpan.Zero)
        );

        await store.WriteEntryAsync(originalEntry);

        var result = await store.ReadAsync();

        Assert.Equal(SwitchJournalReadStatus.Valid, result.Status);
        Assert.NotNull(result.Entry);
        Assert.Equal(SwitchJournalConstants.Magic, result.Entry.Magic);
        Assert.Equal(SwitchJournalConstants.CurrentSchemaVersion, result.Entry.SchemaVersion);
        Assert.Equal(originalEntry.TransactionId, result.Entry.TransactionId);
        Assert.Equal(SwitchJournalState.CREDENTIAL_APPLYING, result.Entry.State);
        Assert.Equal(originalEntry.SourceAccountId, result.Entry.SourceAccountId);
        Assert.Equal(originalEntry.TargetAccountId, result.Entry.TargetAccountId);
        Assert.Equal("TEST_REASON_CODE", result.Entry.QuarantineReasonCode);
        Assert.Equal(originalEntry.UpdatedAt, result.Entry.UpdatedAt);
    }

    // 2. Exact canonical serialized state values for all five states
    [Theory]
    [InlineData(SwitchJournalState.RECORDED, "RECORDED")]
    [InlineData(SwitchJournalState.CREDENTIAL_APPLYING, "CREDENTIAL_APPLYING")]
    [InlineData(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, "TARGET_IDENTITY_VERIFIED_PRECOMMIT")]
    [InlineData(SwitchJournalState.ROLLING_BACK, "ROLLING_BACK")]
    [InlineData(SwitchJournalState.QUARANTINED, "QUARANTINED")]
    public async Task Scenario02_CanonicalSerializedStateValues_AllFiveStates_MatchExactStrings(
        SwitchJournalState state,
        string expectedSerializedState)
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        var entry = CreateSampleEntry(state: state);
        await store.WriteEntryAsync(entry);

        string rawJson = await File.ReadAllTextAsync(path);
        Assert.Contains($"\"state\": \"{expectedSerializedState}\"", rawJson);

        var result = await store.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, result.Status);
        Assert.NotNull(result.Entry);
        Assert.Equal(state, result.Entry.State);
    }

    // 3. Expected minimal property set and zero secret/session/email fields in serialized JSON
    [Fact]
    public async Task Scenario03_SerializedJson_ZeroSecrets_AndMinimalPropertySet()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        var entry = CreateSampleEntry();
        await store.WriteEntryAsync(entry);

        string rawJson = await File.ReadAllTextAsync(path);

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        var allowedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "magic",
            "schemaVersion",
            "transactionId",
            "state",
            "updatedAt",
            "sourceAccountId",
            "targetAccountId",
            "quarantineReasonCode"
        };

        var actualProperties = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            actualProperties.Add(property.Name);
            Assert.Contains(property.Name, allowedProperties);
        }

        Assert.Equal(allowedProperties.Count, actualProperties.Count);

        string[] forbiddenSubstrings =
        [
            "secret", "token", "password", "session", "blob", "wincred", "dpapi",
            "credential", "email", "name", "display", "process", "pid"
        ];

        foreach (string forbidden in forbiddenSubstrings)
        {
            Assert.DoesNotContain(forbidden, rawJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    // 4. Missing file -> Absent classification
    [Fact]
    public async Task Scenario04_Read_WhenFileMissing_ReturnsAbsentClassification()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        Assert.False(File.Exists(path));

        var result = await store.ReadAsync();

        Assert.Equal(SwitchJournalReadStatus.Absent, result.Status);
        Assert.Null(result.Entry);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.Exception);
    }

    // 5. Malformed JSON -> Corrupt classification and file bytes unchanged
    [Fact]
    public async Task Scenario05_Read_WhenMalformedJson_ReturnsCorrupt_AndPreservesFileBytes()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        byte[] malformedBytes = "{\ninvalid json : true, [unclosed"u8.ToArray();
        await File.WriteAllBytesAsync(path, malformedBytes);

        var result = await store.ReadAsync();

        Assert.Equal(SwitchJournalReadStatus.Corrupt, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Null(result.Entry);

        Assert.True(File.Exists(path));
        byte[] currentBytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(malformedBytes, currentBytes);
    }

    // 6. Truncated JSON -> Corrupt classification and file bytes unchanged
    [Theory]
    [InlineData(new byte[0])] // Zero-byte file
    [InlineData(new byte[] { (byte)'{', (byte)'"', (byte)'m', (byte)'a' })] // Truncated start
    public async Task Scenario06_Read_WhenTruncatedJsonOrZeroByte_ReturnsCorrupt_AndPreservesFileBytes(byte[] truncatedBytes)
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        await File.WriteAllBytesAsync(path, truncatedBytes);

        var result = await store.ReadAsync();

        Assert.Equal(SwitchJournalReadStatus.Corrupt, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Null(result.Entry);

        Assert.True(File.Exists(path));
        byte[] currentBytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(truncatedBytes, currentBytes);
    }

    // 7. Wrong magic -> UnsupportedVersion classification and file bytes unchanged
    [Fact]
    public async Task Scenario07_Read_WhenWrongMagic_ReturnsUnsupportedVersion_AndPreservesFileBytes()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        string wrongMagicJson = """
        {
          "magic": "WRONG_MAGIC_VALUE",
          "schemaVersion": 1,
          "transactionId": "11111111-2222-3333-4444-555555555555",
          "state": "RECORDED",
          "updatedAt": "2026-09-27T12:00:00.0000000Z",
          "sourceAccountId": "acc-1",
          "targetAccountId": "acc-2",
          "quarantineReasonCode": null
        }
        """;

        byte[] originalBytes = System.Text.Encoding.UTF8.GetBytes(wrongMagicJson);
        await File.WriteAllBytesAsync(path, originalBytes);

        var result = await store.ReadAsync();

        Assert.Equal(SwitchJournalReadStatus.UnsupportedVersion, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("WRONG_MAGIC_VALUE", result.ErrorMessage);
        Assert.Null(result.Entry);

        Assert.True(File.Exists(path));
        byte[] currentBytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(originalBytes, currentBytes);
    }

    // 8. Unsupported future schema version -> UnsupportedVersion classification and file bytes unchanged
    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    public async Task Scenario08_Read_WhenUnsupportedFutureSchemaVersion_ReturnsUnsupportedVersion_AndPreservesFileBytes(int futureVersion)
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        string futureVersionJson = $$"""
        {
          "magic": "AG2SWITCHJRNL",
          "schemaVersion": {{futureVersion}},
          "transactionId": "11111111-2222-3333-4444-555555555555",
          "state": "RECORDED",
          "updatedAt": "2026-09-27T12:00:00.0000000Z",
          "sourceAccountId": "acc-1",
          "targetAccountId": "acc-2",
          "quarantineReasonCode": null
        }
        """;

        byte[] originalBytes = System.Text.Encoding.UTF8.GetBytes(futureVersionJson);
        await File.WriteAllBytesAsync(path, originalBytes);

        var result = await store.ReadAsync();

        Assert.Equal(SwitchJournalReadStatus.UnsupportedVersion, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains(futureVersion.ToString(), result.ErrorMessage);
        Assert.Null(result.Entry);

        Assert.True(File.Exists(path));
        byte[] currentBytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(originalBytes, currentBytes);
    }

    // 9. Valid JSON with missing required fields -> Corrupt classification without file rewrite
    [Theory]
    [InlineData("transactionId")]
    [InlineData("state")]
    [InlineData("sourceAccountId")]
    [InlineData("targetAccountId")]
    [InlineData("updatedAt")]
    [InlineData("magic")]
    [InlineData("schemaVersion")]
    public async Task Scenario09_Read_WhenMissingRequiredFields_ReturnsCorrupt_WithoutFileRewrite(string missingField)
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        var dict = new Dictionary<string, object?>
        {
            ["magic"] = "AG2SWITCHJRNL",
            ["schemaVersion"] = 1,
            ["transactionId"] = Guid.NewGuid().ToString("D"),
            ["state"] = "RECORDED",
            ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["sourceAccountId"] = "acc-src",
            ["targetAccountId"] = "acc-tgt",
            ["quarantineReasonCode"] = null
        };

        dict.Remove(missingField);
        string json = JsonSerializer.Serialize(dict);
        byte[] originalBytes = System.Text.Encoding.UTF8.GetBytes(json);
        await File.WriteAllBytesAsync(path, originalBytes);

        var result = await store.ReadAsync();

        Assert.Equal(SwitchJournalReadStatus.Corrupt, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains(missingField, result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Entry);

        Assert.True(File.Exists(path));
        byte[] currentBytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(originalBytes, currentBytes);
    }

    // 10. Write creates new journal using the injected durable writer
    [Fact]
    public async Task Scenario10_Write_CreatesNewJournal_UsingInjectedDurableWriter()
    {
        string path = CreateJournalPath();
        var spyWriter = new TestDurableFileWriterSpy();
        var store = new SwitchJournalStore(path, spyWriter);

        var entry = CreateSampleEntry();
        await store.WriteEntryAsync(entry);

        Assert.NotNull(spyWriter.WrittenPath);
        Assert.Equal(Path.GetFullPath(path), spyWriter.WrittenPath);
        Assert.NotNull(spyWriter.WrittenContent);
        Assert.Contains(SwitchJournalConstants.Magic, spyWriter.WrittenContent);
        Assert.Contains(entry.TransactionId, spyWriter.WrittenContent);
    }

    // 11. Second write replaces prior journal using durable writer abstraction
    [Fact]
    public async Task Scenario11_SecondWrite_ReplacesPriorJournal_UsingDurableWriter()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        var entry1 = CreateSampleEntry(state: SwitchJournalState.RECORDED);
        await store.WriteEntryAsync(entry1);

        var read1 = await store.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, read1.Status);
        Assert.Equal(SwitchJournalState.RECORDED, read1.Entry!.State);

        var entry2 = entry1 with { State = SwitchJournalState.CREDENTIAL_APPLYING };
        await store.WriteEntryAsync(entry2);

        var read2 = await store.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, read2.Status);
        Assert.Equal(SwitchJournalState.CREDENTIAL_APPLYING, read2.Entry!.State);

        string rawText = await File.ReadAllTextAsync(path);
        Assert.Contains("\"state\": \"CREDENTIAL_APPLYING\"", rawText);
        Assert.DoesNotContain("\"state\": \"RECORDED\"", rawText);
    }

    // 12. Write validation failure throws ArgumentException and does NOT replace previously valid journal on disk
    [Fact]
    public async Task Scenario12_WriteValidationFailure_ThrowsArgumentException_AndDoesNotReplacePriorJournal()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        var validEntry = CreateSampleEntry(state: SwitchJournalState.RECORDED, targetAccountId: "acc-tgt-original");
        await store.WriteEntryAsync(validEntry);

        byte[] originalBytes = await File.ReadAllBytesAsync(path);

        // 1. Empty TargetAccountId
        var invalidTarget = validEntry with { TargetAccountId = "" };
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteEntryAsync(invalidTarget));

        // 2. Empty SourceAccountId
        var invalidSource = validEntry with { SourceAccountId = "   " };
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteEntryAsync(invalidSource));

        // 3. Invalid UUID TransactionId
        var invalidTxId = validEntry with { TransactionId = "not-a-uuid" };
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteEntryAsync(invalidTxId));

        // 4. Invalid State enum
        var invalidState = validEntry with { State = (SwitchJournalState)999 };
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteEntryAsync(invalidState));

        // Confirm prior journal on disk is byte-for-byte identical
        byte[] currentBytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(originalBytes, currentBytes);

        var readAfterFailures = await store.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, readAfterFailures.Status);
        Assert.Equal(SwitchJournalState.RECORDED, readAfterFailures.Entry!.State);
        Assert.Equal("acc-tgt-original", readAfterFailures.Entry.TargetAccountId);
    }

    // 13. Injected durable-writer failure propagates and is not reported as success
    [Fact]
    public async Task Scenario13_InjectedDurableWriterFailure_PropagatesException()
    {
        string path = CreateJournalPath();
        var failingWriter = new TestDurableFileWriterSpy
        {
            ExceptionToThrow = new IOException("Simulated atomic write disk failure.")
        };
        var store = new SwitchJournalStore(path, failingWriter);

        var entry = CreateSampleEntry();
        var ex = await Assert.ThrowsAsync<IOException>(() => store.WriteEntryAsync(entry));
        Assert.Equal("Simulated atomic write disk failure.", ex.Message);
    }

    // 14. Read I/O failure is not classified as Absent
    [Fact]
    public async Task Scenario14_ReadIoFailure_IsClassifiedAsIoError_NotAbsent()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        var entry = CreateSampleEntry();
        await store.WriteEntryAsync(entry);

        // Lock file with FileShare.None to induce IOException on Read
        await using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await store.ReadAsync();

            Assert.Equal(SwitchJournalReadStatus.IoError, result.Status);
            Assert.NotEqual(SwitchJournalReadStatus.Absent, result.Status);
            Assert.NotEqual(SwitchJournalReadStatus.Corrupt, result.Status);
            Assert.NotNull(result.Exception);
            Assert.IsAssignableFrom<IOException>(result.Exception);
            Assert.Null(result.Entry);
        }
    }

    // 15. Delete existing journal removes the file
    [Fact]
    public async Task Scenario15_Delete_ExistingJournal_RemovesFile()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        var entry = CreateSampleEntry();
        await store.WriteEntryAsync(entry);
        Assert.True(File.Exists(path));

        await store.DeleteAsync();

        Assert.False(File.Exists(path));
    }

    // 16. Delete absent journal is safely idempotent
    [Fact]
    public async Task Scenario16_Delete_AbsentJournal_IsSafelyIdempotent()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);
        Assert.False(File.Exists(path));

        // First deletion on absent file
        await store.DeleteAsync();
        Assert.False(File.Exists(path));

        // Second deletion on absent file
        await store.DeleteAsync();
        Assert.False(File.Exists(path));

        // Also test when directory itself does not exist
        string nonExistentDirPath = Path.Combine(_tempDir, "missing-dir", "switch-journal.json");
        var storeMissingDir = new SwitchJournalStore(nonExistentDirPath);
        await storeMissingDir.DeleteAsync();
    }

    // 17. Deletion failure remains observable if deletion fails
    [Fact]
    public async Task Scenario17_Delete_Failure_RemainsObservable()
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        var entry = CreateSampleEntry();
        await store.WriteEntryAsync(entry);

        // Lock file exclusively to cause File.Delete to fail with IOException on Windows
        await using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.DeleteAsync());
        }

        Assert.True(File.Exists(path));
    }

    // 18. Path is caller-provided and store performs no environment/data-root resolution
    [Fact]
    public async Task Scenario18_PathIsCallerProvided_StorePerformsNoEnvironmentOrDataRootResolution()
    {
        string customJournalPath = Path.Combine(_tempDir, "explicit-custom-journal.json");
        string originalDataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? "";

        try
        {
            // Simulate an environment variable pointing elsewhere
            Environment.SetEnvironmentVariable("DATA_DIR", @"C:\FakeDataDirThatMustNotBeUsed");

            var store = new SwitchJournalStore(customJournalPath);

            Assert.Equal(Path.GetFullPath(customJournalPath), store.JournalFilePath);
            Assert.DoesNotContain("FakeDataDirThatMustNotBeUsed", store.JournalFilePath);

            var entry = CreateSampleEntry();
            await store.WriteEntryAsync(entry);

            Assert.True(File.Exists(customJournalPath));
            Assert.False(Directory.Exists(@"C:\FakeDataDirThatMustNotBeUsed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATA_DIR", string.IsNullOrEmpty(originalDataDir) ? null : originalDataDir);
        }
    }

    // 19. Numeric state token ("state": 0) -> Corrupt classification and file bytes unchanged (LOW-1)
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999)]
    public async Task Scenario19_Read_WhenStateIsNumericToken_ReturnsCorrupt_AndPreservesFileBytes(int numericState)
    {
        string path = CreateJournalPath();
        var store = new SwitchJournalStore(path);

        string numericStateJson = $$"""
        {
          "magic": "AG2SWITCHJRNL",
          "schemaVersion": 1,
          "transactionId": "11111111-2222-3333-4444-555555555555",
          "state": {{numericState}},
          "updatedAt": "2026-09-27T12:00:00.0000000Z",
          "sourceAccountId": "acc-1",
          "targetAccountId": "acc-2",
          "quarantineReasonCode": null
        }
        """;

        byte[] originalBytes = System.Text.Encoding.UTF8.GetBytes(numericStateJson);
        await File.WriteAllBytesAsync(path, originalBytes);

        var result = await store.ReadAsync();

        Assert.Equal(SwitchJournalReadStatus.Corrupt, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("state", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Entry);

        Assert.True(File.Exists(path));
        byte[] currentBytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(originalBytes, currentBytes);
    }

    private sealed class TestDurableFileWriterSpy : IDurableFileWriter
    {
        public string? WrittenPath { get; private set; }
        public string? WrittenContent { get; private set; }
        public Exception? ExceptionToThrow { get; set; }

        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            WrittenPath = destinationPath;
            WrittenContent = content;
            return Task.CompletedTask;
        }
    }
}
