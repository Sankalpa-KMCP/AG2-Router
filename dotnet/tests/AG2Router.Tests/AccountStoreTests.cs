using System.IO;
using System.Text.Json;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Persistence;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class AccountStoreTests : IDisposable
{
    private readonly string _tempDir;

    public AccountStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ag2_account_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InMemoryStore_CRUD_And_PrioritySort_Works()
    {
        var store = new InMemoryAccountStore();

        var acc2 = await store.AddAccountAsync(new CreateAccountInput(
            Email: "secondary@example.com",
            Name: "Account 2",
            Priority: 2
        ));

        var acc1 = await store.AddAccountAsync(new CreateAccountInput(
            Email: "primary@example.com",
            Name: "Account 1",
            Priority: 1
        ));

        var accounts = await store.ListAccountsAsync();
        Assert.Equal(2, accounts.Count);
        Assert.Equal("primary@example.com", accounts[0].Email);
        Assert.Equal("secondary@example.com", accounts[1].Email);

        // Case-insensitive lookup
        var byEmail = await store.GetAccountByEmailAsync("PRIMARY@EXAMPLE.COM");
        Assert.NotNull(byEmail);
        Assert.Equal(acc1.Id, byEmail.Id);

        // Duplicate email rejection
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AddAccountAsync(new CreateAccountInput(Email: "Primary@example.com")));

        // Update
        var updated = await store.UpdateAccountAsync(acc1.Id, new UpdateAccountInput(
            Name: "Primary Renamed",
            Priority: 5
        ));
        Assert.NotNull(updated);
        Assert.Equal("Primary Renamed", updated.Name);
        Assert.Equal(5, updated.Priority);

        // Active account tracking
        await store.SetActiveAccountIdAsync(acc1.Id);
        Assert.Equal(acc1.Id, await store.GetActiveAccountIdAsync());

        // Remove active account resets active ID
        bool removed = await store.RemoveAccountAsync(acc1.Id);
        Assert.True(removed);
        Assert.Null(await store.GetActiveAccountIdAsync());
        Assert.Single(await store.ListAccountsAsync());
    }

    [Fact]
    public async Task InMemoryStore_SetActiveAccount_WithNonExistentId_Throws()
    {
        var store = new InMemoryAccountStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetActiveAccountIdAsync("non_existent_id"));
    }

    [Fact]
    public async Task InMemoryStore_CompareExchangeActiveIdDoesNotOverwriteNewerSelection()
    {
        var store = new InMemoryAccountStore();
        var first = await store.AddAccountAsync(new CreateAccountInput(Email: "first@example.com"));
        var second = await store.AddAccountAsync(new CreateAccountInput(Email: "second@example.com"));
        Assert.True(await store.CompareExchangeActiveAccountIdAsync(null, first.Id));
        await store.SetActiveAccountIdAsync(second.Id);
        Assert.False(await store.CompareExchangeActiveAccountIdAsync(first.Id, null));
        Assert.Equal(second.Id, await store.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task LocalMetadataStore_PersistsToDiskAndReloadsAcrossInstances()
    {
        string filePath = Path.Combine(_tempDir, "accounts.json");
        var store1 = new LocalMetadataAccountStore(filePath);

        var acc = await store1.AddAccountAsync(new CreateAccountInput(
            Email: "persisted@example.com",
            Name: "Persisted Account",
            Priority: 1,
            IsReserve: true,
            Notes: "Persistent storage test"
        ));
        await store1.SetActiveAccountIdAsync(acc.Id);

        Assert.True(File.Exists(filePath));

        // Create fresh store instance on same file path
        var store2 = new LocalMetadataAccountStore(filePath);
        var loadedAccounts = await store2.ListAccountsAsync();
        Assert.Single(loadedAccounts);
        Assert.Equal(acc.Id, loadedAccounts[0].Id);
        Assert.Equal("persisted@example.com", loadedAccounts[0].Email);
        Assert.True(loadedAccounts[0].IsReserve);
        Assert.Equal(acc.Id, await store2.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task LocalMetadataStore_RecoversGracefullyFromMissingFile()
    {
        string filePath = Path.Combine(_tempDir, "non_existent_accounts.json");
        var store = new LocalMetadataAccountStore(filePath);

        var accounts = await store.ListAccountsAsync();
        Assert.Empty(accounts);
        Assert.Null(await store.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task LocalMetadataStore_WritesExactCamelCaseSharedSchema()
    {
        string filePath = Path.Combine(_tempDir, "accounts.json");
        var store = new LocalMetadataAccountStore(filePath);
        var account = await store.AddAccountAsync(new CreateAccountInput(
            Email: "writer@example.com",
            Name: "C# Writer",
            Priority: 0,
            IsReserve: false,
            HasVaultedSession: false,
            Notes: "synthetic"));
        await store.SetActiveAccountIdAsync(account.Id);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(filePath));
        var root = document.RootElement;
        Assert.Equal(["version", "activeAccountId", "accounts"],
            root.EnumerateObject().Select(property => property.Name).ToArray());

        var storedAccount = root.GetProperty("accounts")[0];
        Assert.Equal(
            ["id", "email", "name", "priority", "isReserve", "validationStatus",
             "hasVaultedSession", "createdAt", "updatedAt", "lastActiveAt", "notes"],
            storedAccount.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.False(storedAccount.TryGetProperty("IsActive", out _));
        Assert.False(storedAccount.TryGetProperty("isActive", out _));
        Assert.Equal(0, storedAccount.GetProperty("priority").GetInt32());
        Assert.False(storedAccount.GetProperty("hasVaultedSession").GetBoolean());
    }

    [Fact]
    public async Task LocalMetadataStore_ReadsSharedTypeScriptGoldenFixture()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "accounts-v1.json");
        string filePath = Path.Combine(_tempDir, "accounts.json");
        File.Copy(fixture, filePath);

        var store = new LocalMetadataAccountStore(filePath);
        var accounts = await store.ListAccountsAsync();
        Assert.Equal(2, accounts.Count);
        var account = Assert.Single(accounts, candidate => candidate.Id == "acc_shared01");
        Assert.Equal("acc_shared01", account.Id);
        Assert.Equal("interop@example.com", account.Email);
        Assert.Equal("Interoperability ✓", account.Name);
        Assert.False(account.HasVaultedSession);
        Assert.Null(account.LastActiveAt);
        Assert.Equal(account.Id, await store.GetActiveAccountIdAsync());
        var minimal = Assert.Single(accounts, candidate => candidate.Id == "acc_minimal1");
        Assert.Null(minimal.Name);
        Assert.Null(minimal.Notes);
        Assert.False(minimal.HasVaultedSession);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("{\"version\":1,\"activeAccountId\":null,\"accounts\":[")]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"activeAccountId\":null,\"accounts\":[]}")]
    [InlineData("{\"version\":1,\"activeAccountId\":null,\"accounts\":null}")]
    public async Task LocalMetadataStore_InvalidExistingFileFailsClosedAndPreservesBytes(string content)
    {
        string filePath = Path.Combine(_tempDir, $"invalid-{Guid.NewGuid():N}.json");
        byte[] original = System.Text.Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(filePath, original);
        var store = new LocalMetadataAccountStore(filePath);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.AddAccountAsync(new CreateAccountInput(Email: "blocked@example.com")));

        Assert.Equal(original, await File.ReadAllBytesAsync(filePath));
    }

    [Fact]
    public async Task LocalMetadataStore_PersistenceFailureDoesNotPublishMutation()
    {
        string filePath = Path.Combine(_tempDir, "accounts.json");
        var seedStore = new LocalMetadataAccountStore(filePath);
        var account = await seedStore.AddAccountAsync(new CreateAccountInput(Email: "stable@example.com"));
        byte[] before = await File.ReadAllBytesAsync(filePath);

        var failingStore = new LocalMetadataAccountStore(filePath, new ThrowingFileWriter());
        await Assert.ThrowsAsync<IOException>(() => failingStore.UpdateAccountAsync(
            account.Id,
            new UpdateAccountInput(Name: "must-not-publish")));

        Assert.Equal(before, await File.ReadAllBytesAsync(filePath));
        var observed = await failingStore.GetAccountAsync(account.Id);
        Assert.NotNull(observed);
        Assert.Null(observed.Name);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
    }

    [Fact]
    public async Task LocalMetadataStore_TwoInstancesConcurrentAddsDoNotLoseUpdates()
    {
        string filePath = Path.Combine(_tempDir, "accounts.json");
        var first = new LocalMetadataAccountStore(filePath);
        var second = new LocalMetadataAccountStore(filePath);

        var tasks = Enumerable.Range(0, 20).Select(index =>
            (index % 2 == 0 ? first : second).AddAccountAsync(
                new CreateAccountInput(Email: $"concurrent-{index}@example.com")));
        await Task.WhenAll(tasks);

        var reloaded = new LocalMetadataAccountStore(filePath);
        Assert.Equal(20, (await reloaded.ListAccountsAsync()).Count);
    }

    [Fact]
    public async Task LocalMetadataStore_ConcurrentUpdateDeleteAndAddRebaseOnLatestSnapshot()
    {
        string filePath = Path.Combine(_tempDir, "accounts.json");
        var seed = new LocalMetadataAccountStore(filePath);
        var updateTarget = await seed.AddAccountAsync(new CreateAccountInput(Email: "update@example.com"));
        var deleteTarget = await seed.AddAccountAsync(new CreateAccountInput(Email: "delete@example.com"));

        var first = new LocalMetadataAccountStore(filePath);
        var second = new LocalMetadataAccountStore(filePath);
        var third = new LocalMetadataAccountStore(filePath);
        await Task.WhenAll(
            first.UpdateAccountAsync(updateTarget.Id, new UpdateAccountInput(Name: "updated")),
            second.RemoveAccountAsync(deleteTarget.Id),
            third.AddAccountAsync(new CreateAccountInput(Email: "added@example.com")));

        var accounts = await new LocalMetadataAccountStore(filePath).ListAccountsAsync();
        Assert.Equal("updated", Assert.Single(accounts, account => account.Id == updateTarget.Id).Name);
        Assert.DoesNotContain(accounts, account => account.Id == deleteTarget.Id);
        Assert.Single(accounts, account => account.Email == "added@example.com");
    }

    [Fact]
    public async Task LocalMetadataStore_CompareExchangeActiveIdIsAtomicAcrossInstances()
    {
        string filePath = Path.Combine(_tempDir, "accounts.json");
        var seed = new LocalMetadataAccountStore(filePath);
        var first = await seed.AddAccountAsync(new CreateAccountInput(Email: "first-active@example.com"));
        var second = await seed.AddAccountAsync(new CreateAccountInput(Email: "second-active@example.com"));
        Assert.True(await seed.CompareExchangeActiveAccountIdAsync(null, first.Id));

        var other = new LocalMetadataAccountStore(filePath);
        await other.SetActiveAccountIdAsync(second.Id);
        Assert.False(await seed.CompareExchangeActiveAccountIdAsync(first.Id, null));
        Assert.Equal(second.Id, await seed.GetActiveAccountIdAsync());
    }

    [Fact]
    public void LocalMetadataStore_DefaultPathMatchesSharedDataDirectoryContract()
    {
        string cwd = Path.Combine(_tempDir, "cwd");
        string localRoot = Path.Combine(_tempDir, "local-app-data");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(localRoot, "AG2-Router", "data", "accounts.json")),
            LocalMetadataAccountStore.ResolveDefaultFilePath(
                dataDirectory: string.Empty,
                currentDirectory: cwd,
                localApplicationData: localRoot));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(cwd, "relative-data", "accounts.json")),
            LocalMetadataAccountStore.ResolveDefaultFilePath("relative-data", cwd, localRoot));
        string absoluteData = Path.Combine(_tempDir, "absolute-data");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(absoluteData, "accounts.json")),
            LocalMetadataAccountStore.ResolveDefaultFilePath(absoluteData, cwd, localRoot));
    }

    private sealed class ThrowingFileWriter : IDurableFileWriter
    {
        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken) =>
            throw new IOException("Injected pre-replacement persistence failure.");
    }
}
