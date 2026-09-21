using System.IO;
using AG2Router.AG2.Accounts;
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
}
