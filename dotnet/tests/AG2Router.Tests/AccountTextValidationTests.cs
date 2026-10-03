using System.IO;
using System.Text.Json;
using AG2Router.AG2.Accounts;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed class AccountTextValidationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ag2-text-" + Guid.NewGuid().ToString("N"));
    public AccountTextValidationTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    public static IEnumerable<object[]> Boundaries()
    {
        foreach (var (field, maximum) in new[] { ("name", 256), ("alias", 64), ("notes", 2048) })
        foreach (bool durable in new[] { false, true })
        foreach (bool unicode in new[] { false, true })
            yield return [field, maximum, durable, unicode];
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task StoreBounds_CreateUpdateAndFinalize_AreAtomic(string field, int maximum, bool durable, bool unicode)
    {
        string path = Path.Combine(_directory, "accounts.json");
        IAccountStore store = durable ? new LocalMetadataAccountStore(path) : new InMemoryAccountStore();
        string value = unicode ? string.Concat(Enumerable.Repeat("😀", maximum / 2)) : new string('x', maximum);
        CreateAccountInput Create(string text, string email) => new(email,
            Name: field == "name" ? text : null, Alias: field == "alias" ? text : null, Notes: field == "notes" ? text : null);
        UpdateAccountInput Update(string text) => new(
            Name: field == "name" ? text : null, Alias: field == "alias" ? text : null, Notes: field == "notes" ? text : null);
        var account = await store.AddAccountAsync(Create(value, "bounds@example.com"));
        Assert.Equal(value, Read(account, field));
        Assert.Equal(value, Read((await store.UpdateAccountAsync(account.Id, Update(value)))!, field));
        await store.SetActiveAccountIdAsync(account.Id);
        Assert.NotNull(await store.TryFinalizeSwitchAsync(account.Id, account.Id, Update(value)));
        foreach (string invalid in new[] { value + "x", new string('z', 16000), new string(' ', maximum + 1) })
        {
            var before = (await store.ListAccountsAsync()).ToArray();
            byte[]? bytes = durable ? await File.ReadAllBytesAsync(path) : null;
            foreach (Func<Task> operation in new Func<Task>[] {
                async () => await store.AddAccountAsync(Create(invalid, "rejected@example.com")),
                async () => await store.UpdateAccountAsync(account.Id, Update(invalid)),
                async () => await store.TryFinalizeSwitchAsync(account.Id, account.Id, Update(invalid)) })
            {
                var error = await Assert.ThrowsAsync<ArgumentException>(operation);
                Assert.Contains("UTF-16 code units", error.Message);
                Assert.True(error.Message.Length < 100);
                Assert.DoesNotContain(invalid, error.Message);
                Assert.Equal(before, await store.ListAccountsAsync());
                Assert.Equal(account.Id, await store.GetActiveAccountIdAsync());
                if (durable) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            }
        }
    }

    [Theory]
    [InlineData("name")]
    [InlineData("alias")]
    [InlineData("notes")]
    public async Task LegacyOversizedMetadata_CanBeReadPreservedAndReplaced(string field)
    {
        string path = Path.Combine(_directory, "accounts.json");
        var store = new LocalMetadataAccountStore(path);
        var account = await store.AddAccountAsync(new CreateAccountInput("legacy@example.com"));
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["accounts"]![0]![field] = new string('z', 16000);
        await File.WriteAllTextAsync(path, document.ToJsonString());
        store = new LocalMetadataAccountStore(path);
        Assert.Equal(16000, Read((await store.GetAccountAsync(account.Id))!, field)!.Length);
        await store.UpdateAccountAsync(account.Id, new UpdateAccountInput(Priority: 3));
        Assert.Equal(16000, Read((await store.GetAccountAsync(account.Id))!, field)!.Length);
        var update = new UpdateAccountInput(Name: field == "name" ? "valid" : null,
            Alias: field == "alias" ? "valid" : null, Notes: field == "notes" ? "valid" : null);
        Assert.Equal("valid", Read((await store.UpdateAccountAsync(account.Id, update))!, field));
    }

    private static string? Read(AccountMetadata account, string field) => field switch
    { "name" => account.Name, "alias" => account.Alias, "notes" => account.Notes, _ => throw new InvalidOperationException() };
}
