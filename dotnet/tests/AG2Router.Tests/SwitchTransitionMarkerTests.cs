using System.IO;
using System.Text;
using System.Text.Json;
using AG2Router.AG2.Switching;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Production-store tests for the durable switch transition marker (PROMPT #032): strict
/// schema parsing, create-if-absent ownership, exact-entry conditional deletion, corruption
/// preservation, ambiguous-publish classification, zero-secret serialization, and the
/// legacy canonical-journal compatibility boundary. Uses actual filesystem persistence in
/// task-owned temporary directories.
/// </summary>
public sealed class SwitchTransitionMarkerTests : IDisposable
{
    private readonly string _tempDir;

    public SwitchTransitionMarkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AG2_TransitionMarker_Tests_" + Guid.NewGuid().ToString("N"));
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

    private static string MarkerPathFor(string journalPath) =>
        SwitchTransitionMarkerStore.PathForJournal(journalPath);

    private static SwitchJournalEntry CreateEntry(
        SwitchJournalState state,
        string? transactionId = null,
        string sourceAccountId = "acc-source-123",
        string targetAccountId = "acc-target-456",
        string? quarantineReason = null,
        SwitchTargetActivationProvenance provenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED)
    {
        return new SwitchJournalEntry
        {
            TransactionId = transactionId ?? Guid.NewGuid().ToString("D"),
            State = state,
            SourceAccountId = sourceAccountId,
            TargetAccountId = targetAccountId,
            QuarantineReasonCode = quarantineReason,
            TargetActivationProvenance = provenance,
            UpdatedAt = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)
        };
    }

    private static SwitchTransitionMarkerEntry CreateMarker(
        SwitchJournalState from = SwitchJournalState.RECORDED,
        SwitchJournalState to = SwitchJournalState.CREDENTIAL_APPLYING,
        string? transactionId = null,
        string? sourceAccountId = null,
        string? targetAccountId = null)
    {
        var txId = transactionId ?? Guid.NewGuid().ToString("D");
        return new SwitchTransitionMarkerEntry
        {
            TransactionId = txId,
            CreatedAt = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero),
            Expected = CreateEntry(from, transactionId: txId, sourceAccountId: sourceAccountId ?? "acc-source-123", targetAccountId: targetAccountId ?? "acc-target-456",
                provenance: from == SwitchJournalState.RECORDED
                    ? SwitchTargetActivationProvenance.NOT_ATTEMPTED
                    : SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED),
            Next = CreateEntry(to, transactionId: txId, sourceAccountId: sourceAccountId ?? "acc-source-123", targetAccountId: targetAccountId ?? "acc-target-456",
                provenance: from == SwitchJournalState.RECORDED && to == SwitchJournalState.ROLLING_BACK
                    ? SwitchTargetActivationProvenance.NOT_ATTEMPTED
                    : SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED)
        };
    }

    // 1. Marker create when absent: durable create-if-absent publication round-trips.
    [Fact]
    public async Task Marker_01_Create_WhenAbsent_Succeeds_AndDurablyReloads()
    {
        string journalPath = CreateJournalPath();
        var store = new SwitchTransitionMarkerStore(MarkerPathFor(journalPath));
        var marker = CreateMarker();

        var created = await store.CreateTransitionMarkerIfAbsentAsync(marker);
        Assert.Equal(SwitchTransitionMarkerWriteStatus.Created, created.Status);
        Assert.NotNull(created.Marker);
        Assert.True(File.Exists(MarkerPathFor(journalPath)));

        // A fresh store instance must reload the exact durable marker.
        var reload = await new SwitchTransitionMarkerStore(MarkerPathFor(journalPath)).ReadTransitionMarkerAsync();
        Assert.Equal(SwitchTransitionMarkerReadStatus.Valid, reload.Status);
        Assert.NotNull(reload.Marker);
        Assert.True(SwitchTransitionMarkerStore.MarkersMatch(reload.Marker, created.Marker));
        Assert.Equal(marker.TransactionId, reload.Marker!.TransactionId);
        Assert.Equal(marker.CreatedAt, reload.Marker.CreatedAt);
        Assert.True(SwitchJournalStore.EntriesMatch(marker.Expected, reload.Marker.Expected));
        Assert.True(SwitchJournalStore.EntriesMatch(marker.Next, reload.Marker.Next));
    }

    // 2. Marker create when a marker already exists: AlreadyExists, original bytes preserved.
    [Fact]
    public async Task Marker_02_Create_WhenMarkerExists_NeverOverwrites()
    {
        string journalPath = CreateJournalPath();
        var store = new SwitchTransitionMarkerStore(MarkerPathFor(journalPath));
        var first = await store.CreateTransitionMarkerIfAbsentAsync(CreateMarker());
        Assert.Equal(SwitchTransitionMarkerWriteStatus.Created, first.Status);
        string originalJson = await File.ReadAllTextAsync(MarkerPathFor(journalPath));

        var second = await store.CreateTransitionMarkerIfAbsentAsync(CreateMarker(
            from: SwitchJournalState.CREDENTIAL_APPLYING,
            to: SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT));

        Assert.Equal(SwitchTransitionMarkerWriteStatus.AlreadyExists, second.Status);
        Assert.Equal(originalJson, await File.ReadAllTextAsync(MarkerPathFor(journalPath)));
    }

    // 3. Corrupt existing marker preserved: create reports AlreadyExists, bytes survive.
    [Fact]
    public async Task Marker_03_Create_WhenCorruptMarkerExists_PreservesCorruptBytes()
    {
        string journalPath = CreateJournalPath();
        const string corruptContent = "{ corrupt transition marker bytes";
        await File.WriteAllTextAsync(MarkerPathFor(journalPath), corruptContent);
        var store = new SwitchTransitionMarkerStore(MarkerPathFor(journalPath));

        var result = await store.CreateTransitionMarkerIfAbsentAsync(CreateMarker());

        Assert.Equal(SwitchTransitionMarkerWriteStatus.AlreadyExists, result.Status);
        Assert.Equal(corruptContent, await File.ReadAllTextAsync(MarkerPathFor(journalPath)));
    }

    // 4. Exact marker delete: the exact owned marker is removed.
    [Fact]
    public async Task Marker_04_Delete_ExactMarker_Deletes()
    {
        string journalPath = CreateJournalPath();
        var store = new SwitchTransitionMarkerStore(MarkerPathFor(journalPath));
        var created = await store.CreateTransitionMarkerIfAbsentAsync(CreateMarker());

        var deleted = await store.DeleteTransitionMarkerIfUnchangedAsync(created.Marker!);

        Assert.Equal(SwitchTransitionMarkerDeleteStatus.Deleted, deleted.Status);
        Assert.False(File.Exists(MarkerPathFor(journalPath)));
    }

    // 5. Replacement marker not deleted: a different marker survives exactly.
    [Fact]
    public async Task Marker_05_Delete_DifferentMarker_NotMatched_AndPreserved()
    {
        string journalPath = CreateJournalPath();
        var store = new SwitchTransitionMarkerStore(MarkerPathFor(journalPath));
        var created = await store.CreateTransitionMarkerIfAbsentAsync(CreateMarker());
        string originalJson = await File.ReadAllTextAsync(MarkerPathFor(journalPath));

        var replacement = CreateMarker(
            from: SwitchJournalState.CREDENTIAL_APPLYING,
            to: SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT,
            transactionId: created.Marker!.TransactionId);
        // Rebuild the replacement so only the marker entry differs, then attempt to delete
        // it with the wrong expected value.
        var wrongExpected = await store.CreateTransitionMarkerIfAbsentAsync(replacement);
        Assert.Equal(SwitchTransitionMarkerWriteStatus.AlreadyExists, wrongExpected.Status);

        var deleteResult = await store.DeleteTransitionMarkerIfUnchangedAsync(replacement);

        Assert.Equal(SwitchTransitionMarkerDeleteStatus.NotMatched, deleteResult.Status);
        Assert.Equal(originalJson, await File.ReadAllTextAsync(MarkerPathFor(journalPath)));
    }

    // 6. Corrupt marker not deleted: unreadable bytes always survive.
    [Fact]
    public async Task Marker_06_Delete_CorruptMarker_NotMatched_AndPreserved()
    {
        string journalPath = CreateJournalPath();
        const string corruptContent = "{ corrupt transition marker bytes";
        await File.WriteAllTextAsync(MarkerPathFor(journalPath), corruptContent);
        var store = new SwitchTransitionMarkerStore(MarkerPathFor(journalPath));

        var deleteResult = await store.DeleteTransitionMarkerIfUnchangedAsync(CreateMarker());

        Assert.Equal(SwitchTransitionMarkerDeleteStatus.NotMatched, deleteResult.Status);
        Assert.Equal(corruptContent, await File.ReadAllTextAsync(MarkerPathFor(journalPath)));
    }

    // 7. Delete when absent reports Absent.
    [Fact]
    public async Task Marker_07_Delete_WhenAbsent_ReportsAbsent()
    {
        string journalPath = CreateJournalPath();
        var store = new SwitchTransitionMarkerStore(MarkerPathFor(journalPath));

        var deleteResult = await store.DeleteTransitionMarkerIfUnchangedAsync(CreateMarker());

        Assert.Equal(SwitchTransitionMarkerDeleteStatus.Absent, deleteResult.Status);
    }

    // 8. Zero-secret marker serialization: exact minimal property set, no secret material.
    [Fact]
    public async Task Marker_08_SerializedJson_ZeroSecrets_AndMinimalPropertySet()
    {
        string journalPath = CreateJournalPath();
        var store = new SwitchTransitionMarkerStore(MarkerPathFor(journalPath));
        var marker = CreateMarker(
            from: SwitchJournalState.CREDENTIAL_APPLYING,
            to: SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT,
            transactionId: "01234567-89ab-cdef-0123-456789abcdef",
            sourceAccountId: "acc-source-zerosample",
            targetAccountId: "acc-target-zerosample");

        var created = await store.CreateTransitionMarkerIfAbsentAsync(marker);
        Assert.Equal(SwitchTransitionMarkerWriteStatus.Created, created.Status);

        string json = await File.ReadAllTextAsync(MarkerPathFor(journalPath));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var propertyNames = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var expectedProperties = new HashSet<string>(StringComparer.Ordinal)
        { "magic", "schemaVersion", "transactionId", "createdAt", "expected", "next" };
        Assert.True(expectedProperties.SetEquals(propertyNames),
            $"Marker property set drifted: {string.Join(',', propertyNames.OrderBy(n => n))}");

        string raw = json.ToLowerInvariant();
        foreach (var forbidden in new[] { "token", "cookie", "authorization", "secret", "password", "credentialblob", "session", "dpapi" })
        {
            Assert.DoesNotContain(forbidden, raw);
        }

        // Nested journal entries carry only the authorized canonical journal fields.
        foreach (var nestedName in new[] { "expected", "next" })
        {
            var nested = root.GetProperty(nestedName);
            var nestedProperties = nested.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var expectedNested = new HashSet<string>(StringComparer.Ordinal)
            { "magic", "schemaVersion", "transactionId", "state", "updatedAt", "sourceAccountId", "targetAccountId", "quarantineReasonCode", "targetActivationProvenance" };
            Assert.True(expectedNested.SetEquals(nestedProperties),
                $"Nested '{nestedName}' property set drifted: {string.Join(',', nestedProperties.OrderBy(n => n))}");
        }
    }

    // 9. Ambiguous marker publish with a proven marker: ownership recovered by readback.
    [Fact]
    public async Task Marker_09_AmbiguousPublish_ProvenMarker_RecoversOwnership()
    {
        string journalPath = CreateJournalPath();
        var store = new SwitchTransitionMarkerStore(MarkerPathFor(journalPath));
        store.AfterPublishHookAsync = () => Task.FromException(new IOException("Ambiguous marker publish"));

        var marker = CreateMarker();
        var result = await store.CreateTransitionMarkerIfAbsentAsync(marker);

        Assert.Equal(SwitchTransitionMarkerWriteStatus.Created, result.Status);
        Assert.NotNull(result.Marker);
        Assert.True(SwitchTransitionMarkerStore.MarkersMatch(result.Marker, marker with { Magic = result.Marker.Magic }));
        var read = await store.ReadTransitionMarkerAsync();
        Assert.Equal(SwitchTransitionMarkerReadStatus.Valid, read.Status);
    }

    // 10. Ambiguous marker publish with an unprovable outcome: fails closed, evidence preserved.
    [Fact]
    public async Task Marker_10_AmbiguousPublish_Unprovable_FailsClosed()
    {
        string journalPath = CreateJournalPath();
        var markerPath = MarkerPathFor(journalPath);
        var store = new SwitchTransitionMarkerStore(markerPath);
        store.AfterPublishHookAsync = async () =>
        {
            await Task.Yield();
            File.Move(markerPath, markerPath + ".orphaned");
            throw new IOException("Ambiguous marker publish: unprovable outcome");
        };

        var result = await store.CreateTransitionMarkerIfAbsentAsync(CreateMarker());

        Assert.Equal(SwitchTransitionMarkerWriteStatus.PersistenceFailure, result.Status);
        Assert.True(File.Exists(markerPath + ".orphaned"));
        Assert.False(File.Exists(markerPath));
    }

    // 11. Parser: wrong magic and wrong schema version are unsupported, never valid.
    [Theory]
    [InlineData("{\"magic\":\"OTHER\",\"schemaVersion\":1}", SwitchTransitionMarkerReadStatus.UnsupportedVersion)]
    [InlineData("{\"magic\":\"AG2SWITCHTRNS\",\"schemaVersion\":2}", SwitchTransitionMarkerReadStatus.UnsupportedVersion)]
    public async Task Marker_11_UnsupportedMagicOrVersion_FailsClosed(string json, SwitchTransitionMarkerReadStatus expected)
    {
        string path = Path.Combine(_tempDir, $"marker-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        var result = await new SwitchTransitionMarkerStore(path).ReadTransitionMarkerAsync();
        Assert.Equal(expected, result.Status);
    }

    // 12. Parser: structurally invalid markers are corrupt (fail closed, never ignored).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"magic\":\"AG2SWITCHTRNS\"}")]
    [InlineData("{\"magic\":\"AG2SWITCHTRNS\",\"schemaVersion\":1,\"transactionId\":\"not-a-guid\",\"createdAt\":\"2026-10-07T12:00:00.000Z\",\"expected\":{},\"next\":{}}")]
    [InlineData("{\"magic\":\"AG2SWITCHTRNS\",\"schemaVersion\":1,\"transactionId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"createdAt\":\"2026-10-07T12:00:00.000Z\"}")]
    public Task Marker_12_StructurallyInvalid_Marker_IsCorrupt(string json)
    {
        var result = SwitchTransitionMarkerStore.ParseMarkerFromText(json);
        Assert.Equal(SwitchTransitionMarkerReadStatus.Corrupt, result.Status);
        return Task.CompletedTask;
    }

    // 13. Parser: a marker whose nested pair is not a real journal transition is corrupt.
    [Fact]
    public void Marker_13_InvalidStatePair_IsCorrupt()
    {
        var txId = Guid.NewGuid().ToString("D");
        var invalid = new SwitchTransitionMarkerEntry
        {
            TransactionId = txId,
            CreatedAt = DateTimeOffset.UtcNow,
            Expected = CreateEntry(SwitchJournalState.QUARANTINED, transactionId: txId),
            Next = CreateEntry(SwitchJournalState.ROLLING_BACK, transactionId: txId)
        };

        var options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        string json = System.Text.Json.JsonSerializer.Serialize(invalid, options);
        var result = SwitchTransitionMarkerStore.ParseMarkerFromText(json);
        Assert.Equal(SwitchTransitionMarkerReadStatus.Corrupt, result.Status);
    }

    // 14. Parser: nested entries bound to a different transaction are corrupt.
    [Fact]
    public void Marker_14_NestedTransactionMismatch_IsCorrupt()
    {
        var marker = CreateMarker(transactionId: "01234567-89ab-cdef-0123-456789abcdef");
        var mismatched = marker with
        {
            Expected = marker.Expected with { TransactionId = "fedcba98-76ab-cdef-0123-456789abcdef" }
        };

        string json = System.Text.Json.JsonSerializer.Serialize(mismatched, SwitchTransitionMarkerStore.SerializerOptions);
        var result = SwitchTransitionMarkerStore.ParseMarkerFromText(json);
        Assert.Equal(SwitchTransitionMarkerReadStatus.Corrupt, result.Status);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("provenance_down")]
    [InlineData("provenance_unknown")]
    [InlineData("recorded_unknown")]
    [InlineData("applying_not_attempted")]
    [InlineData("precommit_not_attempted")]
    [InlineData("rolling_back_changes_provenance")]
    public async Task Marker_SemanticContradictions_AreRejectedByParserAndWriter(string contradiction)
    {
        var marker = CreateMarker(from: SwitchJournalState.CREDENTIAL_APPLYING,
            to: SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT);
        marker = contradiction switch
        {
            "source" => marker with { Next = marker.Next with { SourceAccountId = "other-source" } },
            "target" => marker with { Next = marker.Next with { TargetAccountId = "other-target" } },
            "provenance_down" => marker with { Next = marker.Next with { TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED } },
            "provenance_unknown" => marker with { Next = marker.Next with { TargetActivationProvenance = SwitchTargetActivationProvenance.UNKNOWN } },
            "recorded_unknown" => marker with { Expected = marker.Expected with
                { State = SwitchJournalState.RECORDED, TargetActivationProvenance = SwitchTargetActivationProvenance.UNKNOWN },
                Next = marker.Next with { State = SwitchJournalState.CREDENTIAL_APPLYING } },
            "applying_not_attempted" => marker with { Expected = marker.Expected with { TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED } },
            "precommit_not_attempted" => marker with { Expected = marker.Expected with
                { State = SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED },
                Next = marker.Next with { State = SwitchJournalState.ROLLING_BACK, TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED } },
            _ => marker with { Expected = marker.Expected with { State = SwitchJournalState.ROLLING_BACK },
                Next = marker.Next with { State = SwitchJournalState.QUARANTINED, TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED } }
        };
        var path = MarkerPathFor(CreateJournalPath());
        string bytes = JsonSerializer.Serialize(marker, SwitchTransitionMarkerStore.SerializerOptions);
        Assert.Equal(SwitchTransitionMarkerReadStatus.Corrupt, SwitchTransitionMarkerStore.ParseMarkerFromText(bytes).Status);
        var store = new SwitchTransitionMarkerStore(path);
        Assert.Equal(SwitchTransitionMarkerWriteStatus.PersistenceFailure, (await store.CreateTransitionMarkerIfAbsentAsync(marker)).Status);
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, bytes);
        Assert.Equal(SwitchTransitionMarkerReadStatus.Corrupt, (await store.ReadTransitionMarkerAsync()).Status);
        Assert.Equal(SwitchTransitionMarkerDeleteStatus.NotMatched, (await store.DeleteTransitionMarkerIfUnchangedAsync(marker)).Status);
        Assert.Equal(bytes, await File.ReadAllTextAsync(path));
    }

    public static IEnumerable<object[]> MarkerProvenanceCases()
    {
        var pairs = new[]
        {
            (SwitchJournalState.RECORDED, SwitchJournalState.CREDENTIAL_APPLYING),
            (SwitchJournalState.RECORDED, SwitchJournalState.ROLLING_BACK),
            (SwitchJournalState.CREDENTIAL_APPLYING, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT),
            (SwitchJournalState.CREDENTIAL_APPLYING, SwitchJournalState.ROLLING_BACK),
            (SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, SwitchJournalState.ROLLING_BACK),
            (SwitchJournalState.ROLLING_BACK, SwitchJournalState.QUARANTINED)
        };
        foreach (var (from, to) in pairs)
        foreach (var before in Enum.GetValues<SwitchTargetActivationProvenance>())
        foreach (var after in Enum.GetValues<SwitchTargetActivationProvenance>())
        {
            bool valid = from == SwitchJournalState.RECORDED
                ? before == SwitchTargetActivationProvenance.NOT_ATTEMPTED && after ==
                    (to == SwitchJournalState.CREDENTIAL_APPLYING ? SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED : before)
                : from == SwitchJournalState.ROLLING_BACK
                    ? before != SwitchTargetActivationProvenance.UNKNOWN && after == before
                    : before == SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED && after == before;
            yield return new object[] { from, to, before, after, valid };
        }
    }

    [Theory]
    [MemberData(nameof(MarkerProvenanceCases))]
    public async Task Marker_ProvenanceMatrix_ParserAndPublicationAgree(SwitchJournalState from, SwitchJournalState to,
        SwitchTargetActivationProvenance before, SwitchTargetActivationProvenance after, bool valid)
    {
        var marker = CreateMarker(from, to);
        marker = marker with { Expected = marker.Expected with { TargetActivationProvenance = before },
            Next = marker.Next with { TargetActivationProvenance = after } };
        string bytes = JsonSerializer.Serialize(marker, SwitchTransitionMarkerStore.SerializerOptions);
        Assert.Equal(valid ? SwitchTransitionMarkerReadStatus.Valid : SwitchTransitionMarkerReadStatus.Corrupt,
            SwitchTransitionMarkerStore.ParseMarkerFromText(bytes).Status);
        var path = MarkerPathFor(CreateJournalPath());
        var result = await new SwitchTransitionMarkerStore(path).CreateTransitionMarkerIfAbsentAsync(marker);
        Assert.Equal(valid ? SwitchTransitionMarkerWriteStatus.Created : SwitchTransitionMarkerWriteStatus.PersistenceFailure, result.Status);
        Assert.Equal(valid, File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Marker_ExactOwnership_DifferentNextOrCreatedAt_PreservesOccupant(bool timestampOnly)
    {
        var path = MarkerPathFor(CreateJournalPath());
        var store = new SwitchTransitionMarkerStore(path);
        var original = (await store.CreateTransitionMarkerIfAbsentAsync(CreateMarker())).Marker!;
        var different = timestampOnly ? original with { CreatedAt = original.CreatedAt.AddSeconds(1) }
            : original with { Next = original.Next with { State = SwitchJournalState.ROLLING_BACK,
                TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED } };
        string bytes = JsonSerializer.Serialize(different, SwitchTransitionMarkerStore.SerializerOptions);
        await File.WriteAllTextAsync(path, bytes);
        Assert.False(SwitchTransitionMarkerStore.MarkersMatch(original, different));
        Assert.Equal(SwitchTransitionMarkerDeleteStatus.NotMatched, (await store.DeleteTransitionMarkerIfUnchangedAsync(original)).Status);
        Assert.Equal(bytes, await File.ReadAllTextAsync(path));
        Assert.Equal(SwitchTransitionMarkerDeleteStatus.Deleted, (await store.DeleteTransitionMarkerIfUnchangedAsync(different)).Status);
    }

    // 15. Legacy compatibility: a schema-v1 canonical journal with no marker stays readable,
    //     and the interface default derives the marker path beside the canonical journal.
    [Fact]
    public async Task Marker_15_LegacyCanonicalJournal_WithoutMarker_RemainsReadable()
    {
        string journalPath = CreateJournalPath();
        var journalStore = new SwitchJournalStore(journalPath);
        await journalStore.WriteEntryAsync(CreateEntry(SwitchJournalState.RECORDED));

        Assert.False(File.Exists(MarkerPathFor(journalPath)));
        Assert.Equal(SwitchTransitionMarkerReadStatus.Absent,
            (await journalStore.ReadTransitionMarkerAsync()).Status);

        var journal = await journalStore.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, journal.Status);
        Assert.Equal(SwitchJournalState.RECORDED, journal.Entry!.State);
    }

    // 16. Interface defaults: any ISwitchJournalStore implementer gains file-backed marker
    //     persistence beside its journal path.
    [Fact]
    public async Task Marker_16_InterfaceDefaults_CreateReadAndDeleteBesideJournal()
    {
        string journalPath = CreateJournalPath();
        RecordingJournalStore store = new(journalPath);

        var marker = CreateMarker();
        var created = await ((ISwitchJournalStore)store).CreateTransitionMarkerIfAbsentAsync(marker);
        Assert.Equal(SwitchTransitionMarkerWriteStatus.Created, created.Status);
        Assert.True(File.Exists(MarkerPathFor(journalPath)));

        var read = await ((ISwitchJournalStore)store).ReadTransitionMarkerAsync();
        Assert.Equal(SwitchTransitionMarkerReadStatus.Valid, read.Status);

        var deleted = await ((ISwitchJournalStore)store).DeleteTransitionMarkerIfUnchangedAsync(created.Marker!);
        Assert.Equal(SwitchTransitionMarkerDeleteStatus.Deleted, deleted.Status);
        Assert.False(File.Exists(MarkerPathFor(journalPath)));
    }

    /// <summary>Minimal ISwitchJournalStore implementer that exercises the interface's
    /// default marker members (no marker overrides of its own).</summary>
    private sealed class RecordingJournalStore : ISwitchJournalStore
    {
        private readonly SwitchJournalStore _underlying;

        public RecordingJournalStore(string journalFilePath) =>
            _underlying = new SwitchJournalStore(journalFilePath);

        public string JournalFilePath => _underlying.JournalFilePath;

        public Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            _underlying.ReadAsync(cancellationToken);

        public Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default) =>
            _underlying.WriteEntryAsync(entry, cancellationToken);

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            _underlying.DeleteAsync(cancellationToken);

        public Task<SwitchJournalDeleteResult> DeleteIfUnchangedAsync(SwitchJournalEntry expectedEntry, CancellationToken cancellationToken = default) =>
            _underlying.DeleteIfUnchangedAsync(expectedEntry, cancellationToken);

        public Task<SwitchJournalWriteResult> CreateIfAbsentAsync(SwitchJournalEntry nextEntry, CancellationToken cancellationToken = default) =>
            _underlying.CreateIfAbsentAsync(nextEntry, cancellationToken);

        public Task<SwitchJournalWriteResult> ReplaceIfUnchangedAsync(SwitchJournalEntry expectedEntry, SwitchJournalEntry nextEntry, CancellationToken cancellationToken = default) =>
            _underlying.ReplaceIfUnchangedAsync(expectedEntry, nextEntry, cancellationToken);
    }
}
