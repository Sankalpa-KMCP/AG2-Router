using System.IO;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Unit and integration tests for Slice 2: Durable per-account model quota observations.
/// Covers all 22 required test scenarios: Store operations, active capture via NativeAutoRouter, and persistence safety.
/// </summary>
public class DurableQuotaObservationStoreTests
{
    private static AccountMetadata CreateAccount(string id, string email, int priority = 1, bool isReserve = false)
    {
        return new AccountMetadata(
            Id: id,
            Email: email,
            Name: "User",
            Priority: priority,
            IsReserve: isReserve,
            ValidationStatus: "VALID",
            HasVaultedSession: true,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O")
        );
    }

    #region Store Tests (Scenarios 1-12)

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task Schema_MissingOrNullObservations_FailsClosedWithoutReplacingDocument(int version, bool explicitNull)
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var json = $$"""{"schemaVersion":{{version}},"updatedAt":"2026-09-30T00:00:00Z"{{(explicitNull ? ",\"observations\":null" : "")}}} """;
        await File.WriteAllTextAsync(path, json);
        var store = new DurableQuotaObservationStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllObservationsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetObservationsForAccountAsync("synthetic"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetObservationAsync("synthetic", "model/exact"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RecordObservationsAsync("synthetic",
            [new("synthetic", "model/exact", 0.8, null, DateTimeOffset.Parse("2026-09-30T00:00:00Z"), "synthetic")]));
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Schema_ExplicitEmptyArray_RemainsValidAcrossReload(int version)
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var json = $$"""{"schemaVersion":{{version}},"updatedAt":"2026-09-30T00:00:00Z","observations":[]} """;
        await File.WriteAllTextAsync(path, json);

        Assert.Empty(await new DurableQuotaObservationStore(path).GetAllObservationsAsync());
        var reloaded = new DurableQuotaObservationStore(path);
        Assert.Empty(await reloaded.GetObservationsForAccountAsync("synthetic"));
        Assert.Null(await reloaded.GetObservationAsync("synthetic", "model/exact"));
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Schema_ValidPopulatedDocument_ReadsAndRoundTripsAsVersionTwo(int version)
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var observedAt = DateTimeOffset.Parse("2026-09-30T00:00:00Z");
        var row = new AccountModelQuotaObservation("synthetic", "model/exact", 0.8, null, observedAt, "synthetic");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new QuotaObservationsDocument(version, observedAt, [row])));

        var store = new DurableQuotaObservationStore(path);
        Assert.Equal(row, Assert.Single(await store.GetAllObservationsAsync()));
        var unknown = row with { RemainingFraction = null, ObservedAtUtc = observedAt.AddSeconds(1) };
        await store.RecordObservationsAsync(row.AccountId, [unknown]);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("observations").ValueKind);
        Assert.Equal(unknown, Assert.Single(await new DurableQuotaObservationStore(path).GetAllObservationsAsync()));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"observations\":[")]
    [InlineData("{\"schemaVersion\":2,\"observations\":{}}")]
    [InlineData("{\"schemaVersion\":2,\"observations\":[null]}")]
    [InlineData("null")]
    public async Task Schema_MalformedDocument_RemainsRejected(string json)
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => new DurableQuotaObservationStore(path).GetAllObservationsAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Schema_DocumentConstructionRejectsNullButAcceptsExplicitEmptyCollection()
    {
        var updatedAt = DateTimeOffset.Parse("2026-09-30T00:00:00Z");
        Assert.Throws<ArgumentNullException>(() => new QuotaObservationsDocument(2, updatedAt, null));
        Assert.Empty(new QuotaObservationsDocument(2, updatedAt, []).Observations);
    }

    [Fact]
    public async Task Scenario01_Store_MissingFile_ReturnsEmptyCleanly()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "nonexistent-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var all = await store.GetAllObservationsAsync();
        var forAccount = await store.GetObservationsForAccountAsync("acc_1");
        var single = await store.GetObservationAsync("acc_1", "gemini-2.5-pro");

        Assert.Empty(all);
        Assert.Empty(forAccount);
        Assert.Null(single);
    }

    [Fact]
    public async Task Scenario02_Store_OneObservation_RoundTripsSuccessfully()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var observedAt = DateTimeOffset.UtcNow;
        var obs = new AccountModelQuotaObservation(
            "acc_1",
            "gemini-2.5-pro",
            0.85,
            "2026-09-29T18:00:00Z",
            observedAt,
            "ActiveGetUserStatus"
        );

        await store.RecordObservationsAsync("acc_1", [obs]);

        var all = await store.GetAllObservationsAsync();
        var single = await store.GetObservationAsync("acc_1", "gemini-2.5-pro");

        Assert.Single(all);
        Assert.NotNull(single);
        Assert.Equal("acc_1", single.AccountId);
        Assert.Equal("gemini-2.5-pro", single.ModelKey);
        Assert.Equal(0.85, single.RemainingFraction);
        Assert.Equal("2026-09-29T18:00:00Z", single.ResetTime);
        Assert.Equal(observedAt, single.ObservedAtUtc);
        Assert.Equal("ActiveGetUserStatus", single.Source);
    }

    [Fact]
    public async Task Scenario03_Store_MultipleAccounts_ArePreserved()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var now = DateTimeOffset.UtcNow;
        var obs1 = new AccountModelQuotaObservation("acc_1", "gemini-2.5-pro", 0.5, null, now, "Test");
        var obs2 = new AccountModelQuotaObservation("acc_2", "gemini-2.5-flash", 0.9, null, now, "Test");

        await store.RecordObservationsAsync("acc_1", [obs1]);
        await store.RecordObservationsAsync("acc_2", [obs2]);

        var all = await store.GetAllObservationsAsync();
        var acc1Obs = await store.GetObservationsForAccountAsync("acc_1");
        var acc2Obs = await store.GetObservationsForAccountAsync("acc_2");

        Assert.Equal(2, all.Count);
        Assert.Single(acc1Obs);
        Assert.Equal("acc_1", acc1Obs[0].AccountId);
        Assert.Single(acc2Obs);
        Assert.Equal("acc_2", acc2Obs[0].AccountId);
    }

    [Fact]
    public async Task Scenario04_Store_MultipleModels_ArePreserved()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var now = DateTimeOffset.UtcNow;
        var obs1 = new AccountModelQuotaObservation("acc_1", "gemini-2.5-pro", 0.4, null, now, "Test");
        var obs2 = new AccountModelQuotaObservation("acc_1", "gemini-2.5-flash", 0.8, null, now, "Test");

        await store.RecordObservationsAsync("acc_1", [obs1, obs2]);

        var forAcc1 = await store.GetObservationsForAccountAsync("acc_1");
        Assert.Equal(2, forAcc1.Count);
        Assert.Contains(forAcc1, o => o.ModelKey == "gemini-2.5-pro" && o.RemainingFraction == 0.4);
        Assert.Contains(forAcc1, o => o.ModelKey == "gemini-2.5-flash" && o.RemainingFraction == 0.8);
    }

    [Fact]
    public async Task Scenario05_Store_UpdateOneAccountAndModel_PreservesOthers()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var now = DateTimeOffset.UtcNow;
        await store.RecordObservationsAsync("acc_1", [
            new AccountModelQuotaObservation("acc_1", "gemini-pro", 0.5, null, now, "Test"),
            new AccountModelQuotaObservation("acc_1", "gemini-flash", 0.8, null, now, "Test")
        ]);
        await store.RecordObservationsAsync("acc_2", [
            new AccountModelQuotaObservation("acc_2", "gemini-pro", 0.9, null, now, "Test")
        ]);

        // Update only acc_1's gemini-pro
        await store.RecordObservationsAsync("acc_1", [
            new AccountModelQuotaObservation("acc_1", "gemini-pro", 0.1, null, now.AddMinutes(1), "TestUpdate")
        ]);

        var acc1Pro = await store.GetObservationAsync("acc_1", "gemini-pro");
        var acc1Flash = await store.GetObservationAsync("acc_1", "gemini-flash");
        var acc2Pro = await store.GetObservationAsync("acc_2", "gemini-pro");

        Assert.NotNull(acc1Pro);
        Assert.Equal(0.1, acc1Pro.RemainingFraction);
        Assert.Equal("TestUpdate", acc1Pro.Source);

        Assert.NotNull(acc1Flash);
        Assert.Equal(0.8, acc1Flash.RemainingFraction);

        Assert.NotNull(acc2Pro);
        Assert.Equal(0.9, acc2Pro.RemainingFraction);
    }

    [Fact]
    public async Task Scenario06_Store_RestartReload_PreservesExactValues()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store1 = new DurableQuotaObservationStore(path);

        var timestamp = new DateTimeOffset(2026, 9, 29, 14, 30, 0, TimeSpan.Zero);
        var obs = new AccountModelQuotaObservation("acc_42", "gemini-2.5-pro", 0.42, "2026-09-29T20:00:00Z", timestamp, "ProvenanceXYZ");
        await store1.RecordObservationsAsync("acc_42", [obs]);

        // New instance simulates restart / process reload
        var store2 = new DurableQuotaObservationStore(path);
        var reloaded = await store2.GetObservationAsync("acc_42", "gemini-2.5-pro");

        Assert.NotNull(reloaded);
        Assert.Equal("acc_42", reloaded.AccountId);
        Assert.Equal("gemini-2.5-pro", reloaded.ModelKey);
        Assert.Equal(0.42, reloaded.RemainingFraction);
        Assert.Equal("2026-09-29T20:00:00Z", reloaded.ResetTime);
        Assert.Equal(timestamp, reloaded.ObservedAtUtc);
        Assert.Equal("ProvenanceXYZ", reloaded.Source);
    }

    [Fact]
    public async Task Scenario07_Store_CorruptJson_FailsClosed_ThrowsInvalidDataException()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        await File.WriteAllTextAsync(path, "{ corrupt json syntax !!@@ ");

        var store = new DurableQuotaObservationStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllObservationsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetObservationAsync("acc_1", "gemini-pro"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RecordObservationsAsync("acc_1", [
            new AccountModelQuotaObservation("acc_1", "gemini-pro", 0.5, null, DateTimeOffset.UtcNow, "Test")
        ]));
    }

    [Fact]
    public async Task Scenario08_Store_UnsupportedSchemaVersion_FailsClosed_ThrowsInvalidDataException()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var unsupportedJson = "{\"schemaVersion\": 99, \"updatedAt\": \"2026-09-29T00:00:00Z\", \"observations\": []}";
        await File.WriteAllTextAsync(path, unsupportedJson);

        var store = new DurableQuotaObservationStore(path);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllObservationsAsync());
        Assert.Contains("Unsupported quota observation store schema version", ex.Message);
    }

    [Fact]
    public void Scenario09_Store_NaNAndInfinity_AreRejected()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AccountModelQuotaObservation("acc_1", "gemini-pro", double.NaN, null, now, "Test"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AccountModelQuotaObservation("acc_1", "gemini-pro", double.PositiveInfinity, null, now, "Test"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AccountModelQuotaObservation("acc_1", "gemini-pro", double.NegativeInfinity, null, now, "Test"));
    }

    [Fact]
    public void Scenario10_Store_InvalidFraction_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AccountModelQuotaObservation("acc_1", "gemini-pro", -0.001, null, now, "Test"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AccountModelQuotaObservation("acc_1", "gemini-pro", 1.001, null, now, "Test"));
    }

    [Fact]
    public async Task Scenario11_Store_CanonicalModelKey_StorageAndRetrieval()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var now = DateTimeOffset.UtcNow;
        var obs = new AccountModelQuotaObservation("acc_1", "  GEMINI-2.5-PRO  ", 0.7, null, now, "Test");
        await store.RecordObservationsAsync("acc_1", [obs]);

        // File inspection: serialized JSON has canonical lowercase key
        var fileContent = await File.ReadAllTextAsync(path);
        Assert.Contains("\"gemini-2.5-pro\"", fileContent);
        Assert.DoesNotContain("GEMINI-2.5-PRO", fileContent);

        // Retrieval with mixed case and leading/trailing whitespace
        var retrievedCanonical = await store.GetObservationAsync("acc_1", "gemini-2.5-pro");
        var retrievedWhitespace = await store.GetObservationAsync("acc_1", "   GEMINI-2.5-PRO   ");

        Assert.NotNull(retrievedCanonical);
        Assert.NotNull(retrievedWhitespace);
        Assert.Equal("gemini-2.5-pro", retrievedCanonical.ModelKey);
        Assert.Equal("gemini-2.5-pro", retrievedWhitespace.ModelKey);
    }

    [Fact]
    public async Task Scenario12_Store_Serialization_ContainsNoSecretFields()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var now = DateTimeOffset.UtcNow;
        var obs = new AccountModelQuotaObservation("acc_sec", "gemini-pro", 0.5, "2026-09-29T12:00:00Z", now, "LiveTelemetry");
        await store.RecordObservationsAsync("acc_sec", [obs]);

        var fileContent = await File.ReadAllTextAsync(path);

        var forbiddenPatterns = new[] { "password", "token", "secret", "credential", "wincred", "dpapi", "@" };
        foreach (var pattern in forbiddenPatterns)
        {
            Assert.DoesNotContain(pattern, fileContent, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region Active Capture Tests (Scenarios 13-19)

    [Fact]
    public async Task Scenario13_ActiveCapture_ProvenActiveAccount_ValidTelemetry_WritesObservations()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var accountStore = new FakeAccountStore();
        var sessionVault = new FakeSessionVault();
        var adapter = new MockAG2Adapter();
        var coordinator = new FakeSwitchCoordinator();

        var acc = CreateAccount("acc_act", "active@example.com");
        accountStore.Accounts[acc.Id] = acc;
        accountStore.ActiveAccountId = acc.Id;
        sessionVault.StoredIds.Add(acc.Id);

        adapter.RequestedModelOrTier = "gemini-pro";
        adapter.GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(acc.Email));
        adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.65, null, false) },
            null, null
        ));
        adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        await using var router = new NativeAutoRouter(
            accountStore, sessionVault, adapter, coordinator,
            quotaObservationStore: store
        );

        await router.EvaluateCycleAsync();

        var obs = await store.GetObservationAsync("acc_act", "gemini-pro");
        Assert.NotNull(obs);
        Assert.Equal("acc_act", obs.AccountId);
        Assert.Equal("gemini-pro", obs.ModelKey);
        Assert.Equal(0.65, obs.RemainingFraction);
        Assert.Equal("ActiveGetUserStatus", obs.Source);
    }

    [Fact]
    public async Task Scenario14_ActiveCapture_TwoModelRows_ProducesTwoModelObservations()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var accountStore = new FakeAccountStore();
        var sessionVault = new FakeSessionVault();
        var adapter = new MockAG2Adapter();
        var coordinator = new FakeSwitchCoordinator();

        var acc = CreateAccount("acc_dual", "dual@example.com");
        accountStore.Accounts[acc.Id] = acc;
        accountStore.ActiveAccountId = acc.Id;
        sessionVault.StoredIds.Add(acc.Id);

        adapter.RequestedModelOrTier = "gemini-pro";
        adapter.GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(acc.Email));
        adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> {
                new("Gemini Pro", "gemini-pro", 0.45, null, false),
                new("Gemini Flash", "gemini-flash", 0.90, null, false)
            },
            null, null
        ));
        adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        await using var router = new NativeAutoRouter(
            accountStore, sessionVault, adapter, coordinator,
            quotaObservationStore: store
        );

        await router.EvaluateCycleAsync();

        var observations = await store.GetObservationsForAccountAsync("acc_dual");
        Assert.Equal(2, observations.Count);
        Assert.Contains(observations, o => o.ModelKey == "gemini-pro" && o.RemainingFraction == 0.45);
        Assert.Contains(observations, o => o.ModelKey == "gemini-flash" && o.RemainingFraction == 0.90);
    }

    [Fact]
    public async Task Scenario15_ActiveCapture_SubsequentTelemetry_UpdatesSameAccountModel()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var accountStore = new FakeAccountStore();
        var sessionVault = new FakeSessionVault();
        var adapter = new MockAG2Adapter();
        var coordinator = new FakeSwitchCoordinator();

        var acc = CreateAccount("acc_seq", "seq@example.com");
        accountStore.Accounts[acc.Id] = acc;
        accountStore.ActiveAccountId = acc.Id;
        sessionVault.StoredIds.Add(acc.Id);

        adapter.RequestedModelOrTier = "gemini-pro";
        adapter.GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(acc.Email));
        adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        // Cycle 1: 0.70
        adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.70, null, false) },
            null, null
        ));

        await using var router = new NativeAutoRouter(
            accountStore, sessionVault, adapter, coordinator,
            quotaObservationStore: store
        );

        await router.EvaluateCycleAsync();
        var obs1 = await store.GetObservationAsync("acc_seq", "gemini-pro");
        Assert.Equal(0.70, obs1!.RemainingFraction);

        // Cycle 2: updated to 0.25
        adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.25, null, false) },
            null, null
        ));

        await router.EvaluateCycleAsync();
        var obs2 = await store.GetObservationAsync("acc_seq", "gemini-pro");
        Assert.Equal(0.25, obs2!.RemainingFraction);

        // Ensure not duplicated
        var all = await store.GetObservationsForAccountAsync("acc_seq");
        Assert.Single(all);
    }

    [Fact]
    public async Task Scenario16_ActiveCapture_AccountATelemetry_CannotBeStoredUnderAccountB()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var now = DateTimeOffset.UtcNow;
        var obsA = new AccountModelQuotaObservation("acc_A", "gemini-pro", 0.5, null, now, "Test");

        // Direct store check: store enforces obs.AccountId == target accountId
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RecordObservationsAsync("acc_B", [obsA]));
        Assert.Contains("does not match target accountId", ex.Message);

        // Router check: telemetry for active account A is recorded under A, never under B
        var accountStore = new FakeAccountStore();
        var sessionVault = new FakeSessionVault();
        var adapter = new MockAG2Adapter();
        var coordinator = new FakeSwitchCoordinator();

        var accA = CreateAccount("acc_A", "a@example.com");
        var accB = CreateAccount("acc_B", "b@example.com");
        accountStore.Accounts[accA.Id] = accA;
        accountStore.Accounts[accB.Id] = accB;
        accountStore.ActiveAccountId = accA.Id;
        sessionVault.StoredIds.Add(accA.Id);
        sessionVault.StoredIds.Add(accB.Id);

        adapter.RequestedModelOrTier = "gemini-pro";
        adapter.GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(accA.Email));
        adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.5, null, false) },
            null, null
        ));
        adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        await using var router = new NativeAutoRouter(
            accountStore, sessionVault, adapter, coordinator,
            quotaObservationStore: store
        );

        await router.EvaluateCycleAsync();

        Assert.NotNull(await store.GetObservationAsync("acc_A", "gemini-pro"));
        Assert.Empty(await store.GetObservationsForAccountAsync("acc_B"));
    }

    [Fact]
    public async Task Scenario17_ActiveCapture_UnknownOrUnprovenActiveIdentity_WritesNothing()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var accountStore = new FakeAccountStore();
        var sessionVault = new FakeSessionVault();
        var adapter = new MockAG2Adapter();
        var coordinator = new FakeSwitchCoordinator();

        var acc = CreateAccount("acc_known", "known@example.com");
        accountStore.Accounts[acc.Id] = acc;
        accountStore.ActiveAccountId = acc.Id;
        sessionVault.StoredIds.Add(acc.Id);

        // Live adapter returns unproven (different email) identity
        adapter.RequestedModelOrTier = "gemini-pro";
        adapter.GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto("unknown@example.com"));
        adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.5, null, false) },
            null, null
        ));
        adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        await using var router = new NativeAutoRouter(
            accountStore, sessionVault, adapter, coordinator,
            quotaObservationStore: store
        );

        await router.EvaluateCycleAsync();

        // Verify nothing written
        var all = await store.GetAllObservationsAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task Scenario18_ActiveCapture_InvalidOrUnknownQuota_WritesNoHealthyEvidence()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var accountStore = new FakeAccountStore();
        var sessionVault = new FakeSessionVault();
        var adapter = new MockAG2Adapter();
        var coordinator = new FakeSwitchCoordinator();

        var acc = CreateAccount("acc_inv", "inv@example.com");
        accountStore.Accounts[acc.Id] = acc;
        accountStore.ActiveAccountId = acc.Id;
        sessionVault.StoredIds.Add(acc.Id);

        adapter.RequestedModelOrTier = "gemini-pro";
        adapter.GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(acc.Email));
        adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));

        // Unknown is persisted explicitly so it supersedes older healthy evidence.
        // Model 2: exhausted with null fraction -> recorded as 0.0 (not healthy)
        adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> {
                new("Unknown Model", "gemini-unknown", null, null, false),
                new("Exhausted Model", "gemini-exhausted", null, null, true)
            },
            null, null
        ));
        adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        await using var router = new NativeAutoRouter(
            accountStore, sessionVault, adapter, coordinator,
            quotaObservationStore: store
        );

        await router.EvaluateCycleAsync();

        var unknownObs = await store.GetObservationAsync("acc_inv", "gemini-unknown");
        Assert.NotNull(unknownObs);
        Assert.Null(unknownObs.RemainingFraction);

        var exhaustedObs = await store.GetObservationAsync("acc_inv", "gemini-exhausted");
        Assert.NotNull(exhaustedObs);
        Assert.Equal(0.0, exhaustedObs.RemainingFraction); // 0.0 fraction, not healthy
    }

    [Fact]
    public async Task Scenario19_ActiveCapture_ResetTime_PersistsUnchangedWhenPresent()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var accountStore = new FakeAccountStore();
        var sessionVault = new FakeSessionVault();
        var adapter = new MockAG2Adapter();
        var coordinator = new FakeSwitchCoordinator();

        var acc = CreateAccount("acc_reset", "reset@example.com");
        accountStore.Accounts[acc.Id] = acc;
        accountStore.ActiveAccountId = acc.Id;
        sessionVault.StoredIds.Add(acc.Id);

        const string expectedResetTime = "2026-09-29T18:30:00.000Z";
        adapter.RequestedModelOrTier = "gemini-pro";
        adapter.GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(acc.Email));
        adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> {
                new("Gemini Pro", "gemini-pro", 0.5, expectedResetTime, false)
            },
            null, null
        ));
        adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        await using var router = new NativeAutoRouter(
            accountStore, sessionVault, adapter, coordinator,
            quotaObservationStore: store
        );

        await router.EvaluateCycleAsync();

        var obs = await store.GetObservationAsync("acc_reset", "gemini-pro");
        Assert.NotNull(obs);
        Assert.Equal(expectedResetTime, obs.ResetTime);
    }

    #endregion

    #region Persistence Safety Tests (Scenarios 20-22)

    [Fact]
    public async Task Scenario20_PersistenceSafety_AtomicWritePath_UsesRepositoryNativeDurableWriter()
    {
        var spy = new SpyDurableFileWriter();
        var targetPath = Path.Combine(Path.GetTempPath(), $"quota_test_{Guid.NewGuid():N}.json");
        var store = new DurableQuotaObservationStore(targetPath, spy);

        var now = DateTimeOffset.UtcNow;
        var obs = new AccountModelQuotaObservation("acc_1", "gemini-pro", 0.5, null, now, "Test");
        await store.RecordObservationsAsync("acc_1", [obs]);

        Assert.True(spy.WriteAtomicCalled);
        Assert.Equal(Path.GetFullPath(targetPath), spy.LastDestinationPath);
        Assert.NotNull(spy.LastContent);
        Assert.Contains("gemini-pro", spy.LastContent);
    }

    [Fact]
    public void Scenario21_PersistenceSafety_DataDirOverride_IsHonored()
    {
        var customDir = Path.Combine(Path.GetTempPath(), $"custom_dir_{Guid.NewGuid():N}");
        var resolvedExplicit = DurableQuotaObservationStore.ResolveDefaultFilePath(dataDirectory: customDir);
        Assert.Equal(Path.Combine(customDir, "quota-observations.json"), resolvedExplicit);

        Environment.SetEnvironmentVariable("DATA_DIR", customDir);
        try
        {
            var resolvedEnv = DurableQuotaObservationStore.ResolveDefaultFilePath();
            Assert.Equal(Path.Combine(customDir, "quota-observations.json"), resolvedEnv);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATA_DIR", null);
        }
    }

    [Fact]
    public async Task Scenario22_PersistenceSafety_SequentialUpdates_DoNotLoseUnrelatedObservations()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "quota-observations.json");
        var store = new DurableQuotaObservationStore(path);

        var now = DateTimeOffset.UtcNow;

        // Step 1: acc_1 + gemini-pro
        await store.RecordObservationsAsync("acc_1", [
            new AccountModelQuotaObservation("acc_1", "gemini-pro", 0.5, null, now, "Step1")
        ]);

        // Step 2: acc_1 + gemini-flash
        await store.RecordObservationsAsync("acc_1", [
            new AccountModelQuotaObservation("acc_1", "gemini-flash", 0.8, null, now, "Step2")
        ]);

        // Step 3: acc_2 + gemini-pro
        await store.RecordObservationsAsync("acc_2", [
            new AccountModelQuotaObservation("acc_2", "gemini-pro", 0.9, null, now, "Step3")
        ]);

        var allBefore = await store.GetAllObservationsAsync();
        Assert.Equal(3, allBefore.Count);

        // Step 4: update acc_1 + gemini-pro to 0.2
        await store.RecordObservationsAsync("acc_1", [
            new AccountModelQuotaObservation("acc_1", "gemini-pro", 0.2, null, now.AddMinutes(5), "Step4")
        ]);

        var allAfter = await store.GetAllObservationsAsync();
        Assert.Equal(3, allAfter.Count);

        var acc1Pro = await store.GetObservationAsync("acc_1", "gemini-pro");
        var acc1Flash = await store.GetObservationAsync("acc_1", "gemini-flash");
        var acc2Pro = await store.GetObservationAsync("acc_2", "gemini-pro");

        Assert.NotNull(acc1Pro);
        Assert.Equal(0.2, acc1Pro.RemainingFraction);
        Assert.Equal("Step4", acc1Pro.Source);

        Assert.NotNull(acc1Flash);
        Assert.Equal(0.8, acc1Flash.RemainingFraction);
        Assert.Equal("Step2", acc1Flash.Source);

        Assert.NotNull(acc2Pro);
        Assert.Equal(0.9, acc2Pro.RemainingFraction);
        Assert.Equal("Step3", acc2Pro.Source);
    }

    #endregion

    #region Test Doubles & Fixtures

    private sealed class TestTempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ag2_test_obs_{Guid.NewGuid():N}");

        public TestTempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors on temp dir
            }
        }
    }

    private sealed class SpyDurableFileWriter : IDurableFileWriter
    {
        public bool WriteAtomicCalled { get; private set; }
        public string? LastDestinationPath { get; private set; }
        public string? LastContent { get; private set; }
        public Exception? ExceptionToThrow { get; set; }

        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            WriteAtomicCalled = true;
            LastDestinationPath = destinationPath;
            LastContent = content;

            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccountStore : IAccountStore
    {
        public Dictionary<string, AccountMetadata> Accounts { get; } = new(StringComparer.Ordinal);
        public string? ActiveAccountId { get; set; }

        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AccountMetadata>>(Accounts.Values.ToList());

        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts.TryGetValue(id, out var a) ? a : null);

        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts.Values.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ActiveAccountId);

        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default)
        {
            ActiveAccountId = id;
            return Task.CompletedTask;
        }

        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RemoveAccountIfUnchangedAsync(AccountMetadata expected, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> CompareExchangeActiveAccountIdAsync(string? expectedId, string? newId, CancellationToken cancellationToken = default)
        {
            if (ActiveAccountId == expectedId)
            {
                ActiveAccountId = newId;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<AccountMetadata?> TryFinalizeSwitchAsync(string? expectedActiveId, string targetId, UpdateAccountInput updates, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeSessionVault : ISessionVault, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ag2_test_obs_vault_{Guid.NewGuid():N}");
        public HashSet<string> StoredIds { get; } = new(StringComparer.Ordinal);

        public FakeSessionVault()
        {
            Directory.CreateDirectory(_directory);
        }

        public Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Contains(accountId));

        public Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(StoredIds.Contains(accountId) ? new byte[] { 1, 2, 3 } : null);

        public Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Remove(accountId));

        public Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(StoredIds.ToList());

        public string GetVaultPath() => Path.Combine(_directory, "vault");

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                try { Directory.Delete(_directory, recursive: true); } catch { }
            }
        }
    }

    private sealed class FakeSwitchCoordinator : INativeAccountSwitchCoordinator
    {
        public NativeSwitchStatus GetStatus() => new(null, NativeSwitchStates.Idle, null, false, JournalRecoveryStates.None);

        public Task<JournalResolutionResult> ResolveQuarantinedJournalAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalResolutionResult(JournalResolutionStatus.NoJournal, "No switch journal present."));

        public Task<NativeSwitchResult> SwitchAsync(string targetAccountId, CancellationToken cancellationToken = default)
            => Task.FromResult(new NativeSwitchResult(
                TransactionId: "tx_123",
                Success: true,
                Code: SwitchResultCodes.Success,
                State: NativeSwitchStates.Complete,
                TargetAccountId: targetAccountId,
                TargetEmail: "target@example.com",
                PreviousAccountId: "acc_current",
                PreviousEmail: "curr@example.com",
                Message: SwitchResultCodes.Success,
                StagesCompleted: Array.Empty<string>(),
                StartedAt: DateTimeOffset.UtcNow.ToString("O"),
                FinishedAt: DateTimeOffset.UtcNow.ToString("O"),
                ManualRecoveryRequired: false
            ));

        public Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    #endregion
}
