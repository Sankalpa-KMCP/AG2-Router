using System.IO;
using System.Text;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Vault;
using AG2Router.Core.Models;
using AG2Router.Windows.Security;
using Xunit;

namespace AG2Router.Tests;

public class SessionVaultTests : IDisposable
{
    private readonly string _tempVaultDir;

    public SessionVaultTests()
    {
        _tempVaultDir = Path.Combine(Path.GetTempPath(), $"ag2_vault_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempVaultDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempVaultDir))
        {
            try { Directory.Delete(_tempVaultDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EmptyVault_ReturnsNullAndFalse_AndListsEmpty()
    {
        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_tempVaultDir, fakeDpapi);

        Assert.False(await vault.HasSessionAsync("non_existent_id"));
        Assert.Null(await vault.GetSessionAsync("non_existent_id"));
        var ids = await vault.ListStoredAccountIdsAsync();
        Assert.Empty(ids);
    }

    [Fact]
    public async Task SaveAndGetSession_WithFakeDpapi_RoundTripsSuccessfully()
    {
        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_tempVaultDir, fakeDpapi);

        byte[] sessionBlob = Encoding.UTF8.GetBytes("{\"token\":\"test-session-token-12345\"}");
        await vault.SaveSessionAsync("acc_test_1", sessionBlob, "gemini:antigravity");

        Assert.True(await vault.HasSessionAsync("acc_test_1"));
        var retrieved = await vault.GetSessionAsync("acc_test_1");

        Assert.NotNull(retrieved);
        Assert.Equal(sessionBlob, retrieved);

        var ids = await vault.ListStoredAccountIdsAsync();
        Assert.Single(ids);
        Assert.Contains("acc_test_1", ids);

        // Verify sessions.dat exists on disk and has magic
        string vaultPath = vault.GetVaultPath();
        Assert.True(File.Exists(vaultPath));
        string rawJson = File.ReadAllText(vaultPath);
        Assert.Contains(VaultConstants.Magic, rawJson);
        Assert.DoesNotContain("test-session-token-12345", rawJson); // Secret is encrypted at rest
    }

    [Fact]
    public async Task SaveAndGetSession_WithRealWindowsDpapi_RoundTripsInTempDir()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dpapi = new WindowsDpapiProvider();
        var vault = new SessionVault(_tempVaultDir, dpapi);

        byte[] sessionBlob = Encoding.UTF8.GetBytes("{\"token\":\"real-dpapi-token-abcde\"}");
        await vault.SaveSessionAsync("acc_real_dpapi", sessionBlob, "gemini:antigravity");

        var retrieved = await vault.GetSessionAsync("acc_real_dpapi");
        Assert.NotNull(retrieved);
        Assert.Equal(sessionBlob, retrieved);
    }

    [Fact]
    public async Task SaveSession_UpsertsExistingRecord_PreservingCreatedAt()
    {
        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_tempVaultDir, fakeDpapi);

        byte[] blob1 = Encoding.UTF8.GetBytes("session-v1");
        await vault.SaveSessionAsync("acc_upsert", blob1);

        // Read envelope to check createdAt
        string raw1 = File.ReadAllText(vault.GetVaultPath());
        var env1 = JsonSerializer.Deserialize<VaultFileEnvelope>(raw1);
        string createdAt1 = env1!.Records["acc_upsert"].CreatedAt;

        await Task.Delay(15);

        byte[] blob2 = Encoding.UTF8.GetBytes("session-v2");
        await vault.SaveSessionAsync("acc_upsert", blob2);

        string raw2 = File.ReadAllText(vault.GetVaultPath());
        var env2 = JsonSerializer.Deserialize<VaultFileEnvelope>(raw2);
        string createdAt2 = env2!.Records["acc_upsert"].CreatedAt;
        string updatedAt2 = env2.Records["acc_upsert"].UpdatedAt;

        Assert.Equal(createdAt1, createdAt2);
        Assert.True(string.CompareOrdinal(updatedAt2, createdAt1) >= 0);

        var retrieved = await vault.GetSessionAsync("acc_upsert");
        Assert.Equal(blob2, retrieved);
    }

    [Fact]
    public async Task RemoveSession_RemovesRecordAndReturnsTrue()
    {
        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_tempVaultDir, fakeDpapi);

        byte[] blob = Encoding.UTF8.GetBytes("session-to-delete");
        await vault.SaveSessionAsync("acc_delete_me", blob);

        Assert.True(await vault.HasSessionAsync("acc_delete_me"));
        bool removed = await vault.RemoveSessionAsync("acc_delete_me");
        Assert.True(removed);

        Assert.False(await vault.HasSessionAsync("acc_delete_me"));
        Assert.Null(await vault.GetSessionAsync("acc_delete_me"));

        // Removing non-existent returns false
        Assert.False(await vault.RemoveSessionAsync("acc_delete_me"));
    }

    [Fact]
    public async Task GetSession_WithFramingAccountIdMismatch_ThrowsVaultCorruptionException()
    {
        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_tempVaultDir, fakeDpapi);

        // Manually create a swapped record where key in envelope != inner payload accountId
        var framed = new VaultedSessionPlaintext(
            Version: 1,
            AccountId: "acc_real_owner",
            Target: "gemini:antigravity",
            CredentialBlobBase64: Convert.ToBase64String("token"u8.ToArray()),
            EnrolledAt: DateTime.UtcNow.ToString("o")
        );
        byte[] plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(framed));
        byte[] ciphertext = await fakeDpapi.EncryptAsync(plaintext);

        var envelope = new VaultFileEnvelope(
            Magic: VaultConstants.Magic,
            SchemaVersion: 1,
            UpdatedAt: DateTime.UtcNow.ToString("o"),
            Records: new Dictionary<string, VaultAccountRecord>
            {
                ["acc_attacker_key"] = new VaultAccountRecord(
                    AccountId: "acc_attacker_key", // Mismatch with inner "acc_real_owner"
                    Target: "gemini:antigravity",
                    EncryptedPayloadBase64: Convert.ToBase64String(ciphertext),
                    CreatedAt: DateTime.UtcNow.ToString("o"),
                    UpdatedAt: DateTime.UtcNow.ToString("o")
                )
            }
        );

        File.WriteAllText(vault.GetVaultPath(), JsonSerializer.Serialize(envelope));

        var ex = await Assert.ThrowsAsync<VaultCorruptionException>(() => vault.GetSessionAsync("acc_attacker_key"));
        Assert.Contains("Identity framing violation", ex.Message);
    }

    [Fact]
    public async Task GetSession_WithFramingTargetMismatch_ThrowsVaultCorruptionException()
    {
        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_tempVaultDir, fakeDpapi);

        var framed = new VaultedSessionPlaintext(
            Version: 1,
            AccountId: "acc_target_test",
            Target: "gemini:original_target",
            CredentialBlobBase64: Convert.ToBase64String("token"u8.ToArray()),
            EnrolledAt: DateTime.UtcNow.ToString("o")
        );
        byte[] plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(framed));
        byte[] ciphertext = await fakeDpapi.EncryptAsync(plaintext);

        var envelope = new VaultFileEnvelope(
            Magic: VaultConstants.Magic,
            SchemaVersion: 1,
            UpdatedAt: DateTime.UtcNow.ToString("o"),
            Records: new Dictionary<string, VaultAccountRecord>
            {
                ["acc_target_test"] = new VaultAccountRecord(
                    AccountId: "acc_target_test",
                    Target: "gemini:swapped_target", // Target mismatch
                    EncryptedPayloadBase64: Convert.ToBase64String(ciphertext),
                    CreatedAt: DateTime.UtcNow.ToString("o"),
                    UpdatedAt: DateTime.UtcNow.ToString("o")
                )
            }
        );

        File.WriteAllText(vault.GetVaultPath(), JsonSerializer.Serialize(envelope));

        var ex = await Assert.ThrowsAsync<VaultCorruptionException>(() => vault.GetSessionAsync("acc_target_test"));
        Assert.Contains("Target mismatch", ex.Message);
    }

    [Fact]
    public async Task CorruptedVaultFile_FailsClosedAndNeverOverwrites()
    {
        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_tempVaultDir, fakeDpapi);

        // Write non-JSON corrupt bytes
        string corruptedContent = "<<<CORRUPTED_BINARY_DATA>>>";
        File.WriteAllText(vault.GetVaultPath(), corruptedContent);

        // All operations must fail closed
        await Assert.ThrowsAsync<VaultCorruptionException>(() => vault.GetSessionAsync("any_id"));
        await Assert.ThrowsAsync<VaultCorruptionException>(() => vault.HasSessionAsync("any_id"));
        await Assert.ThrowsAsync<VaultCorruptionException>(() => vault.ListStoredAccountIdsAsync());
        await Assert.ThrowsAsync<VaultCorruptionException>(() => vault.SaveSessionAsync("new_id", "blob"u8.ToArray()));

        // Confirm the corrupted file was NOT wiped or overwritten
        Assert.Equal(corruptedContent, File.ReadAllText(vault.GetVaultPath()));
    }

    [Fact]
    public async Task WrongMagicOrVersion_ThrowsVaultCorruptionException()
    {
        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_tempVaultDir, fakeDpapi);

        var wrongMagic = new VaultFileEnvelope(
            Magic: "WRONG_MAGIC",
            SchemaVersion: 1,
            UpdatedAt: DateTime.UtcNow.ToString("o"),
            Records: new()
        );
        File.WriteAllText(vault.GetVaultPath(), JsonSerializer.Serialize(wrongMagic));
        await Assert.ThrowsAsync<VaultCorruptionException>(() => vault.ListStoredAccountIdsAsync());

        var wrongVersion = new VaultFileEnvelope(
            Magic: VaultConstants.Magic,
            SchemaVersion: 99,
            UpdatedAt: DateTime.UtcNow.ToString("o"),
            Records: new()
        );
        File.WriteAllText(vault.GetVaultPath(), JsonSerializer.Serialize(wrongVersion));
        await Assert.ThrowsAsync<VaultCorruptionException>(() => vault.ListStoredAccountIdsAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("{\"magic\":\"AG2_ROUTER_SESSION_VAULT\"")]
    public async Task EmptyWhitespaceOrTruncatedVaultFailsClosedAndPreservesBytes(string content)
    {
        var vault = new SessionVault(_tempVaultDir, new FakeDpapiProvider());
        byte[] original = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(vault.GetVaultPath(), original);

        await Assert.ThrowsAsync<VaultCorruptionException>(() =>
            vault.SaveSessionAsync("acc_blocked", "synthetic"u8.ToArray()));

        Assert.Equal(original, await File.ReadAllBytesAsync(vault.GetVaultPath()));
    }

    [Fact]
    public async Task PersistenceFailurePreservesPreviousVaultSnapshot()
    {
        var dpapi = new FakeDpapiProvider();
        var seed = new SessionVault(_tempVaultDir, dpapi);
        await seed.SaveSessionAsync("acc_stable", "stable-session"u8.ToArray());
        byte[] before = await File.ReadAllBytesAsync(seed.GetVaultPath());

        var failing = new SessionVault(_tempVaultDir, dpapi, new ThrowingFileWriter());
        await Assert.ThrowsAsync<VaultException>(() =>
            failing.SaveSessionAsync("acc_new", "new-session"u8.ToArray()));

        Assert.Equal(before, await File.ReadAllBytesAsync(seed.GetVaultPath()));
        Assert.Equal("stable-session"u8.ToArray(), await seed.GetSessionAsync("acc_stable"));
        Assert.False(await seed.HasSessionAsync("acc_new"));
    }

    [Fact]
    public async Task TwoVaultInstancesConcurrentSavesDoNotLoseUpdates()
    {
        var dpapi = new FakeDpapiProvider();
        var first = new SessionVault(_tempVaultDir, dpapi);
        var second = new SessionVault(_tempVaultDir, dpapi);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            (index % 2 == 0 ? first : second).SaveSessionAsync(
                $"acc_{index}",
                Encoding.UTF8.GetBytes($"session-{index}"))));

        var reloaded = new SessionVault(_tempVaultDir, dpapi);
        Assert.Equal(20, (await reloaded.ListStoredAccountIdsAsync()).Count);
    }

    [Fact]
    public async Task RestoreIfCurrent_DoesNotOverwriteNewerRecord()
    {
        var dpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_tempVaultDir, dpapi);
        await vault.SaveSessionAsync("acc_receipt", "original"u8.ToArray());
        var receipt = await vault.SaveSessionWithReceiptAsync("acc_receipt", "candidate"u8.ToArray());
        await vault.SaveSessionAsync("acc_receipt", "newer"u8.ToArray());

        Assert.False(await vault.RestoreIfCurrentAsync(receipt));
        Assert.Equal("newer"u8.ToArray(), await vault.GetSessionAsync("acc_receipt"));
    }

    [Fact]
    public async Task RemovalWriterFailureAfterReplacementRestoresPreimage()
    {
        var writer = new ReplaceThenFailOnceWriter();
        var vault = new SessionVault(_tempVaultDir, new FakeDpapiProvider(), writer);
        await vault.SaveSessionAsync("acc_uncertain", "synthetic-session"u8.ToArray());

        await Assert.ThrowsAsync<VaultException>(() => vault.RemoveSessionWithReceiptAsync("acc_uncertain"));
        Assert.Equal("synthetic-session"u8.ToArray(), await vault.GetSessionAsync("acc_uncertain"));
    }

    [Fact]
    public async Task NeverSettlingWriterReturnsUncertaintyAndBoundsLaterVaultAdmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new DelayedWriter(entered, release);
        var vault = new SessionVault(_tempVaultDir, new FakeDpapiProvider(), writer)
        {
            MutationTimeout = TimeSpan.FromSeconds(2),
            AdmissionTimeout = TimeSpan.FromSeconds(2)
        };
        try
        {
            var first = vault.SaveSessionAsync("acc_first", "first"u8.ToArray());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<VaultMutationUncertainException>(
                () => first.WaitAsync(TimeSpan.FromSeconds(5)));

            var later = new SessionVault(_tempVaultDir, new FakeDpapiProvider());
            await Assert.ThrowsAsync<VaultMutationUncertainException>(
                () => later.SaveSessionAsync("acc_later", "later"u8.ToArray())
                    .WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, writer.WriteCount);
        }
        finally
        {
            release.TrySetResult();
        }

        var gate = PathLockRegistry.Get(vault.GetVaultPath());
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
        gate.Release();
        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => vault.SaveSessionAsync("acc_after", "after"u8.ToArray()));
        Assert.Equal(1, writer.WriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimedOutLeaseWaitCannotOverwriteNewerVaultState(bool remove)
    {
        const string accountId = "acc_lease_race";
        var dpapi = new FakeDpapiProvider();
        var newerVault = new SessionVault(Path.Combine(_tempVaultDir, "newer"), dpapi);
        await newerVault.SaveSessionAsync(accountId, "newer-session"u8.ToArray());
        string newerEnvelope = await File.ReadAllTextAsync(newerVault.GetVaultPath());

        var vault = new SessionVault(_tempVaultDir, dpapi);
        if (remove) await vault.SaveSessionAsync(accountId, "older-session"u8.ToArray());
        vault.MutationTimeout = TimeSpan.FromSeconds(2);

        var heldLease = await CrossProcessFileLease.AcquireAsync(vault.GetVaultPath(), CancellationToken.None);
        try
        {
            Task oldOperation = remove
                ? vault.RemoveSessionAsync(accountId)
                : vault.SaveSessionAsync(accountId, "stale-session"u8.ToArray());
            await Assert.ThrowsAsync<VaultMutationUncertainException>(
                () => oldOperation.WaitAsync(TimeSpan.FromSeconds(5)));

            // A different serialized owner publishes the newer state while the old
            // operation is still waiting for this lease.
            await File.WriteAllTextAsync(vault.GetVaultPath(), newerEnvelope);
        }
        finally
        {
            await heldLease.DisposeAsync();
        }

        var gate = PathLockRegistry.Get(vault.GetVaultPath());
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
        gate.Release();
        Assert.Equal(newerEnvelope, await File.ReadAllTextAsync(vault.GetVaultPath()));
    }

    [Fact]
    public async Task RemovalWriterChangingExistingRecordRequiresQuarantine()
    {
        const string accountId = "acc_changed_during_removal";
        var dpapi = new FakeDpapiProvider();
        var newerVault = new SessionVault(Path.Combine(_tempVaultDir, "replacement"), dpapi);
        await newerVault.SaveSessionAsync(accountId, "different-session"u8.ToArray());
        string replacement = await File.ReadAllTextAsync(newerVault.GetVaultPath());

        var vault = new SessionVault(_tempVaultDir, dpapi,
            new ReplaceWithDifferentRecordThenFailWriter(replacement));
        await vault.SaveSessionAsync(accountId, "original-session"u8.ToArray());

        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => vault.RemoveSessionWithReceiptAsync(accountId));
        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => vault.SaveSessionAsync(accountId, "later-session"u8.ToArray()));
        Assert.Equal(replacement, await File.ReadAllTextAsync(vault.GetVaultPath()));
    }

    [Fact]
    public async Task TimedOutRemovalDoesNotStartLateCompensationWrite()
    {
        const string accountId = "acc_late_compensation";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new ReplacePauseThenThrowWriter(entered, release);
        var vault = new SessionVault(_tempVaultDir, new FakeDpapiProvider(), writer);
        await vault.SaveSessionAsync(accountId, "original-session"u8.ToArray());
        vault.MutationTimeout = TimeSpan.FromSeconds(2);

        Task removal = vault.RemoveSessionWithReceiptAsync(accountId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => removal.WaitAsync(TimeSpan.FromSeconds(5)));
        release.TrySetResult();

        var gate = PathLockRegistry.Get(vault.GetVaultPath());
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
        gate.Release();
        Assert.Equal(2, writer.WriteCount);
        await Assert.ThrowsAsync<VaultMutationUncertainException>(
            () => vault.SaveSessionAsync(accountId, "later-session"u8.ToArray()));
    }

    private sealed class ReplacePauseThenThrowWriter(TaskCompletionSource entered,
        TaskCompletionSource release) : IDurableFileWriter
    {
        private readonly DurableFileWriter _inner = new();
        public int WriteCount { get; private set; }

        public async Task WriteAtomicAsync(string destinationPath, string content,
            CancellationToken cancellationToken)
        {
            int write = ++WriteCount;
            await _inner.WriteAtomicAsync(destinationPath, content, cancellationToken);
            if (write != 2) return;
            entered.TrySetResult();
            await release.Task; // deliberately ignores cancellation after replacement
            throw new IOException("Synthetic late removal failure.");
        }
    }

    private sealed class ReplaceWithDifferentRecordThenFailWriter(string replacement) : IDurableFileWriter
    {
        private readonly DurableFileWriter _inner = new();
        private int _writes;
        public async Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _writes) == 2)
            {
                await _inner.WriteAtomicAsync(destinationPath, replacement, cancellationToken);
                throw new IOException("synthetic conflicting removal replacement");
            }
            await _inner.WriteAtomicAsync(destinationPath, content, cancellationToken);
        }
    }

    private sealed class DelayedWriter(TaskCompletionSource entered, TaskCompletionSource release) : IDurableFileWriter
    {
        private readonly DurableFileWriter _inner = new();
        public int WriteCount { get; private set; }
        public async Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            WriteCount++;
            entered.TrySetResult();
            await release.Task; // deliberately ignores cancellation
            await _inner.WriteAtomicAsync(destinationPath, content, CancellationToken.None);
        }
    }

    private sealed class ReplaceThenFailOnceWriter : IDurableFileWriter
    {
        private readonly DurableFileWriter _inner = new();
        private int _writes;
        public async Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            await _inner.WriteAtomicAsync(destinationPath, content, cancellationToken);
            if (Interlocked.Increment(ref _writes) == 2)
                throw new IOException("Injected post-replacement failure.");
        }
    }

    private sealed class ThrowingFileWriter : IDurableFileWriter
    {
        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken) =>
            throw new IOException("Injected pre-replacement persistence failure.");
    }
}
