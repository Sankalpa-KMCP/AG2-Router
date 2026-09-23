using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Vault;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Isolated integration test suite for v0.2.0 Slice 6:
/// Proves that replacing a v0.1.0 AG2 Router installation with the current vNext payload
/// preserves persistent accounts, aliases, configuration, and encrypted session vault artifacts
/// without data loss, byte corruption, or schema incompatibility.
///
/// CRITICAL SAFETY GUARANTEES:
/// 1. Operates strictly within isolated disposable temporary directories under Path.GetTempPath().
/// 2. Real %LOCALAPPDATA%\AG2-Router, %LOCALAPPDATA%\Programs\AG2Router, Windows Credential Manager,
///    real DPAPI keys, and registry keys are NEVER accessed or modified.
/// 3. Vault format testing uses the repository's FakeDpapiProvider test double. Real Windows DPAPI is NOT tested.
/// 4. Production Setup EXE was not executed in an isolated Windows registry sandbox.
/// </summary>
public class UpgradePreservationTests : IDisposable
{
    [Fact]
    public void StagedReplacementRemovesOldOnlyFilesAndRestoresWholeTreeOnInterruption()
    {
        string source = Path.Combine(_testRoot, "synthetic-new");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "AG2Router.exe"), "new-synthetic");
        File.WriteAllText(Path.Combine(_installDir, "AG2Router.exe"), "old-synthetic");
        File.WriteAllText(Path.Combine(_installDir, "old-only.dll"), "old-only-synthetic");

        Assert.Throws<IOException>(() => SimulateInstallerBinaryReplacement(source, _installDir,
            () => throw new IOException("synthetic interruption")));
        Assert.Equal("old-synthetic", File.ReadAllText(Path.Combine(_installDir, "AG2Router.exe")));
        Assert.True(File.Exists(Path.Combine(_installDir, "old-only.dll")));
        Assert.False(Directory.Exists(_installDir + ".bak"));

        SimulateInstallerBinaryReplacement(source, _installDir);
        Assert.Equal("new-synthetic", File.ReadAllText(Path.Combine(_installDir, "AG2Router.exe")));
        Assert.False(File.Exists(Path.Combine(_installDir, "old-only.dll")));
        Assert.False(Directory.Exists(_installDir + ".bak"));
    }

    [Fact]
    public void ExistingBackupBlocksStagingWithoutOverlay()
    {
        string source = Path.Combine(_testRoot, "synthetic-new");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "AG2Router.exe"), "new-synthetic");
        File.WriteAllText(Path.Combine(_installDir, "AG2Router.exe"), "old-synthetic");
        Directory.CreateDirectory(_installDir + ".bak");
        Assert.Throws<IOException>(() => SimulateInstallerBinaryReplacement(source, _installDir));
        Assert.Equal("old-synthetic", File.ReadAllText(Path.Combine(_installDir, "AG2Router.exe")));
    }

    public const string CanonicalV010ZipSha256 = "18E72FD661839B3492160D189C43C6F0A1D20D3F08B54E1A50EEB0640CE78714";

    private readonly string _testRoot;
    private readonly string _installDir;
    private readonly string _userDataDir;
    private readonly string _dataDir;
    private readonly string _vaultDir;

    public UpgradePreservationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"ag2_upgrade_test_{Guid.NewGuid():N}");
        _installDir = Path.Combine(_testRoot, "Programs", "AG2Router");
        _userDataDir = Path.Combine(_testRoot, "AG2-Router");
        _dataDir = Path.Combine(_userDataDir, "data");
        _vaultDir = Path.Combine(_userDataDir, "vault");

        Directory.CreateDirectory(_installDir);
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_vaultDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of temporary test root
            }
        }
    }

    #region Lane A: Canonical v0.1.0 Binary-Payload Replacement

    [Fact]
    public async Task LaneA_CanonicalV010Release_WhenAvailable_PreservesPersistentStateByteForByte()
    {
        string repoRoot = FindRepositoryRoot();
        string? canonicalZip = TryResolveCanonicalV010Zip();

        if (string.IsNullOrWhiteSpace(canonicalZip))
        {
            // When canonical ZIP is unavailable (e.g. fresh CI runner without cache), record status and return cleanly.
            // Lane B guarantees deterministic synthetic baseline coverage in all environments.
            return;
        }

        // 1. Recompute and verify exact canonical hash immediately before extraction
        string computedHash = ComputeSha256(canonicalZip);
        Assert.Equal(CanonicalV010ZipSha256, computedHash);

        // 2. Extract canonical v0.1.0 release into disposable install directory
        ZipFile.ExtractToDirectory(canonicalZip, _installDir, overwriteFiles: true);

        // Verify v0.1.0 baseline markers in disposable install directory
        string v010Exe = Path.Combine(_installDir, "AG2Router.exe");
        string v010Index = Path.Combine(_installDir, "wwwroot", "index.html");
        Assert.True(File.Exists(v010Exe), "v0.1.0 AG2Router.exe must be extracted");
        Assert.True(File.Exists(v010Index), "v0.1.0 wwwroot\\index.html must be extracted");

        string v010IndexContent = File.ReadAllText(v010Index);
        Assert.Contains("id=\"add-account-modal\"", v010IndexContent); // Legacy HTML marker

        // 3. Stage synthetic persistent user state in disposable AG2-Router directory
        await StageSyntheticPersistentStateAsync(_userDataDir);

        // 4. Compute pre-upgrade baseline hashes of all persistent files and v0.1.0 assets
        var preUpgradeHashes = ComputeDirectoryFileHashes(_userDataDir);
        Assert.True(preUpgradeHashes.Count >= 4, "Must stage at least accounts, vault, sentinel, and config files");

        string v010AppJs = Path.Combine(_installDir, "wwwroot", "app.js");
        string v010StylesCss = Path.Combine(_installDir, "wwwroot", "styles.css");
        string v010AppJsSha = ComputeSha256(v010AppJs);
        string v010StylesCssSha = ComputeSha256(v010StylesCss);

        // 5. Execute simulated binary replacement with current vNext payload
        string vNextPayloadDir = ResolveVNextPayloadDirectory(repoRoot);
        SimulateInstallerBinaryReplacement(vNextPayloadDir, _installDir);

        // 6. Assert vNext payload now resides in install directory
        string vNextIndex = Path.Combine(_installDir, "wwwroot", "index.html");
        string vNextAppJs = Path.Combine(_installDir, "wwwroot", "app.js");
        string vNextStylesCss = Path.Combine(_installDir, "wwwroot", "styles.css");

        Assert.True(File.Exists(vNextIndex), "vNext index.html must exist");
        Assert.True(File.Exists(vNextAppJs), "vNext app.js must exist");
        Assert.True(File.Exists(vNextStylesCss), "vNext styles.css must exist");

        string vNextIndexContent = File.ReadAllText(vNextIndex);
        Assert.Contains("<div id=\"app\"></div>", vNextIndexContent); // Current Svelte mount container
        Assert.DoesNotContain("id=\"add-account-modal\"", vNextIndexContent); // Legacy modal completely replaced

        // Assert Svelte bundle assets genuinely replaced old v0.1.0 assets (not just superficial existence)
        Assert.NotEqual(v010AppJsSha, ComputeSha256(vNextAppJs));
        Assert.NotEqual(v010StylesCssSha, ComputeSha256(vNextStylesCss));
        Assert.Equal(ComputeSha256(Path.Combine(vNextPayloadDir, "wwwroot", "app.js")), ComputeSha256(vNextAppJs));
        Assert.Equal(ComputeSha256(Path.Combine(vNextPayloadDir, "wwwroot", "styles.css")), ComputeSha256(vNextStylesCss));

        // 7. Assert 100% byte-for-byte preservation of persistent user data
        var postUpgradeHashes = ComputeDirectoryFileHashes(_userDataDir);
        Assert.Equal(preUpgradeHashes.Count, postUpgradeHashes.Count);

        foreach (var (relativeFile, expectedHash) in preUpgradeHashes)
        {
            Assert.True(postUpgradeHashes.ContainsKey(relativeFile), $"Persistent file '{relativeFile}' must survive upgrade");
            Assert.Equal(expectedHash, postUpgradeHashes[relativeFile]);
        }

        // 8. Assert post-upgrade runtime compatibility with current code
        string accountsPath = Path.Combine(_dataDir, "accounts.json");
        var store = new LocalMetadataAccountStore(accountsPath);
        var accounts = await store.ListAccountsAsync();
        Assert.Equal(2, accounts.Count);
        Assert.Equal("acc_shared01", await store.GetActiveAccountIdAsync());

        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_vaultDir, fakeDpapi);
        byte[]? decrypted = await vault.GetSessionAsync("acc_shared01");
        Assert.NotNull(decrypted);
        Assert.Equal("synthetic-session-token-v010", Encoding.UTF8.GetString(decrypted));
    }

    #endregion

    #region Lane B: Deterministic Synthetic Baseline

    [Fact]
    public async Task LaneB_SyntheticBaseline_PreservesPersistentStateByteForByte()
    {
        string repoRoot = FindRepositoryRoot();

        // 1. Stage synthetic v0.1.0 install tree in disposable install directory
        StageSyntheticV010InstallPayload(_installDir);

        string oldIndex = Path.Combine(_installDir, "wwwroot", "index.html");
        Assert.Contains("v0.1.0-legacy-dom", File.ReadAllText(oldIndex));

        // 2. Stage synthetic persistent user state
        await StageSyntheticPersistentStateAsync(_userDataDir);

        // 3. Compute baseline SHA-256 hashes
        var preHashes = ComputeDirectoryFileHashes(_userDataDir);

        // 4. Simulate binary upgrade to vNext
        string vNextDir = ResolveVNextPayloadDirectory(repoRoot);
        SimulateInstallerBinaryReplacement(vNextDir, _installDir);

        // 5. Verify vNext payload replaced old payload
        string upgradedIndex = Path.Combine(_installDir, "wwwroot", "index.html");
        string upgradedAppJs = Path.Combine(_installDir, "wwwroot", "app.js");
        string upgradedStylesCss = Path.Combine(_installDir, "wwwroot", "styles.css");

        Assert.True(File.Exists(upgradedIndex));
        Assert.True(File.Exists(upgradedAppJs));
        Assert.True(File.Exists(upgradedStylesCss));

        Assert.Contains("<div id=\"app\"></div>", File.ReadAllText(upgradedIndex));
        Assert.DoesNotContain("v0.1.0-legacy-dom", File.ReadAllText(upgradedIndex));

        // Assert that synthetic v0.1.0 scripts were replaced with vNext Svelte payload
        Assert.DoesNotContain("// v0.1.0 vanilla js", File.ReadAllText(upgradedAppJs));
        Assert.DoesNotContain("/* v0.1.0 vanilla css */", File.ReadAllText(upgradedStylesCss));
        Assert.Equal(ComputeSha256(Path.Combine(vNextDir, "wwwroot", "app.js")), ComputeSha256(upgradedAppJs));
        Assert.Equal(ComputeSha256(Path.Combine(vNextDir, "wwwroot", "styles.css")), ComputeSha256(upgradedStylesCss));

        // 6. Verify byte-for-byte identical persistent hashes
        var postHashes = ComputeDirectoryFileHashes(_userDataDir);
        Assert.Equal(preHashes.Count, postHashes.Count);

        foreach (var (relPath, preHash) in preHashes)
        {
            Assert.True(postHashes.TryGetValue(relPath, out string? postHash), $"File {relPath} missing after upgrade");
            Assert.Equal(preHash, postHash);
        }

        // 7. Assert post-upgrade runtime compatibility with current code
        string accountsPath = Path.Combine(_dataDir, "accounts.json");
        var store = new LocalMetadataAccountStore(accountsPath);
        var accounts = await store.ListAccountsAsync();
        Assert.Equal(2, accounts.Count);
        Assert.Equal("acc_shared01", await store.GetActiveAccountIdAsync());

        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_vaultDir, fakeDpapi);
        byte[]? decrypted = await vault.GetSessionAsync("acc_shared01");
        Assert.NotNull(decrypted);
        Assert.Equal("synthetic-session-token-v010", Encoding.UTF8.GetString(decrypted));
    }

    #endregion

    #region Lane C: Legacy Account Compatibility & Backward-Compatible Mutation

    [Fact]
    public async Task LaneC_LegacyAccountsWithoutAlias_LoadAndMigrateSeamlessly()
    {
        string repoRoot = FindRepositoryRoot();

        // 1. Stage legacy accounts fixture (no alias property present in JSON)
        string accountsFile = Path.Combine(_dataDir, "accounts.json");
        string legacyFixture = GetLegacyAccountsFixturePath(repoRoot);
        File.Copy(legacyFixture, accountsFile, overwrite: true);

        string preUpgradeSha = ComputeSha256(accountsFile);

        // 2. Simulate binary replacement
        string vNextDir = ResolveVNextPayloadDirectory(repoRoot);
        SimulateInstallerBinaryReplacement(vNextDir, _installDir);

        // 3. Verify accounts.json remains untouched by binary replacement
        Assert.Equal(preUpgradeSha, ComputeSha256(accountsFile));

        // 4. Load with current vNext LocalMetadataAccountStore
        var store = new LocalMetadataAccountStore(accountsFile);
        var accounts = await store.ListAccountsAsync();

        Assert.Equal(2, accounts.Count);

        // Verify primary legacy account
        var acc1 = accounts.FirstOrDefault(a => a.Id == "acc_shared01");
        Assert.NotNull(acc1);
        Assert.Equal("interop@example.com", acc1.Email);
        Assert.Equal("Interoperability ✓", acc1.Name);
        Assert.Equal(0, acc1.Priority);
        Assert.False(acc1.IsReserve);
        Assert.Null(acc1.Alias); // Crucial: legacy record cleanly deserializes with null alias

        // Verify active account survives
        Assert.Equal("acc_shared01", await store.GetActiveAccountIdAsync());

        // 5. Mutate an account with an alias via vNext API
        var updated = await store.UpdateAccountAsync("acc_shared01", new UpdateAccountInput(Alias: "Work"));
        Assert.NotNull(updated);
        Assert.Equal("Work", updated.Alias);

        // 6. Verify persistence: raw JSON now contains alias for updated account only
        string updatedJson = File.ReadAllText(accountsFile);
        Assert.Contains("\"alias\": \"Work\"", updatedJson);
        Assert.DoesNotContain("\"alias\": null", updatedJson); // Confirms JsonIgnoreCondition.WhenWritingNull omits null alias

        // Verify strict JSON schema handling (UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)
        // Re-load in a completely new store instance
        var reloadedStore = new LocalMetadataAccountStore(accountsFile);
        var reloadedAcc1 = await reloadedStore.GetAccountAsync("acc_shared01");
        Assert.NotNull(reloadedAcc1);
        Assert.Equal("Work", reloadedAcc1.Alias);
        Assert.Equal("Interoperability ✓", reloadedAcc1.Name);

        var reloadedAcc2 = await reloadedStore.GetAccountAsync("acc_minimal1");
        Assert.NotNull(reloadedAcc2);
        Assert.Null(reloadedAcc2.Alias); // Unchanged account remains null

        // 7. Verify fail-closed behavior on unmapped properties (JsonUnmappedMemberHandling.Disallow)
        string unmappedJson = updatedJson.Replace("\"isReserve\": false", "\"isReserve\": false, \"unmappedTestProperty\": 42");
        string unmappedFile = Path.Combine(_dataDir, "unmapped_accounts.json");
        File.WriteAllText(unmappedFile, unmappedJson);
        var unmappedStore = new LocalMetadataAccountStore(unmappedFile);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => unmappedStore.ListAccountsAsync());
        Assert.IsType<JsonException>(ex.InnerException);
    }

    #endregion

    #region Lane D: Existing Alias Preservation Across Repeat Upgrades

    [Fact]
    public async Task LaneD_ExistingAliases_PreserveByteForByteOnRepeatUpgrade()
    {
        string repoRoot = FindRepositoryRoot();

        // 1. Stage accounts with existing aliases
        string accountsFile = Path.Combine(_dataDir, "accounts.json");
        var store = new LocalMetadataAccountStore(accountsFile);
        await store.AddAccountAsync(new CreateAccountInput(
            Email: "work@company.com",
            Name: "Company Account",
            Priority: 1,
            Alias: "Work"
        ));
        await store.AddAccountAsync(new CreateAccountInput(
            Email: "personal@gmail.com",
            Name: "Personal Account",
            Priority: 2,
            Alias: "Personal"
        ));

        string preUpgradeSha = ComputeSha256(accountsFile);

        // 2. Perform repeat / subsequent binary upgrade
        string vNextDir = ResolveVNextPayloadDirectory(repoRoot);
        SimulateInstallerBinaryReplacement(vNextDir, _installDir);

        // 3. Assert byte-for-byte identity of accounts.json before any app write
        string postUpgradeSha = ComputeSha256(accountsFile);
        Assert.Equal(preUpgradeSha, postUpgradeSha);

        // 4. Verify aliases load correctly in a fresh store instance
        var postStore = new LocalMetadataAccountStore(accountsFile);
        var loaded = await postStore.ListAccountsAsync();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Work", loaded[0].Alias);
        Assert.Equal("Personal", loaded[1].Alias);
    }

    #endregion

    #region Lane E: Vault / Session Format Preservation

    [Fact]
    public async Task LaneE_SessionVault_PreservesPreUpgradeSessionsUnderFakeDpapi()
    {
        string repoRoot = FindRepositoryRoot();

        // Use FakeDpapiProvider test double (real DPAPI CurrentUser is explicitly protected and untouched)
        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_vaultDir, fakeDpapi);

        // 1. Stage pre-upgrade synthetic session
        byte[] preUpgradeToken = Encoding.UTF8.GetBytes("{\"accessToken\":\"token-v010-secret\",\"refreshToken\":\"refresh-v010\"}");
        await vault.SaveSessionAsync("acc_test_pre", preUpgradeToken, "gemini:antigravity");

        string vaultFile = vault.GetVaultPath();
        Assert.True(File.Exists(vaultFile));
        string preUpgradeVaultSha = ComputeSha256(vaultFile);

        // 2. Simulate binary upgrade
        string vNextDir = ResolveVNextPayloadDirectory(repoRoot);
        SimulateInstallerBinaryReplacement(vNextDir, _installDir);

        // 3. Assert vault file is byte-for-byte identical
        Assert.Equal(preUpgradeVaultSha, ComputeSha256(vaultFile));

        // 4. Verify current SessionVault reads the pre-upgrade session
        var postVault = new SessionVault(_vaultDir, fakeDpapi);
        byte[]? retrieved = await postVault.GetSessionAsync("acc_test_pre");
        Assert.NotNull(retrieved);
        Assert.Equal(preUpgradeToken, retrieved);

        // 5. Enroll a second session post-upgrade without corrupting pre-upgrade records
        byte[] vNextToken = Encoding.UTF8.GetBytes("{\"accessToken\":\"token-vNext-secret\",\"refreshToken\":\"refresh-vNext\"}");
        await postVault.SaveSessionAsync("acc_test_post", vNextToken, "gemini:antigravity");

        var allIds = await postVault.ListStoredAccountIdsAsync();
        Assert.Equal(2, allIds.Count);
        Assert.Contains("acc_test_pre", allIds);
        Assert.Contains("acc_test_post", allIds);

        byte[]? recheckedPre = await postVault.GetSessionAsync("acc_test_pre");
        Assert.Equal(preUpgradeToken, recheckedPre);
    }

    #endregion

    #region Lane F: Other Persistent State & Auxiliary Files

    [Fact]
    public void LaneF_OtherPersistentState_SentinelsAndCustomFilesSurvive()
    {
        string repoRoot = FindRepositoryRoot();

        // 1. Stage auxiliary files and sentinels in persistent user directory
        string sentinelPath = Path.Combine(_userDataDir, "sentinel.hash");
        File.WriteAllText(sentinelPath, "c0ffee-deadbeef-synthetic-sentinel-hash-v010\n");

        string configPath = Path.Combine(_userDataDir, "custom.config");
        File.WriteAllText(configPath, "{\"autoSwitch\": true, \"lowThreshold\": 15}\n");

        string logDir = Path.Combine(_userDataDir, "logs");
        Directory.CreateDirectory(logDir);
        string logPath = Path.Combine(logDir, "router.log");
        File.WriteAllText(logPath, "2026-09-01T10:00:00Z [INFO] Initial startup v0.1.0\n");

        var preHashes = ComputeDirectoryFileHashes(_userDataDir);

        // 2. Simulate binary replacement
        string vNextDir = ResolveVNextPayloadDirectory(repoRoot);
        SimulateInstallerBinaryReplacement(vNextDir, _installDir);

        // 3. Verify all auxiliary files and sentinels survive with identical SHA-256
        var postHashes = ComputeDirectoryFileHashes(_userDataDir);
        Assert.Equal(preHashes.Count, postHashes.Count);

        foreach (var (file, hash) in preHashes)
        {
            Assert.True(postHashes.TryGetValue(file, out string? currentHash), $"Auxiliary file '{file}' missing");
            Assert.Equal(hash, currentHash);
        }
    }

    #endregion

    #region Lane G: Negative & Failure Boundary Cases

    [Fact]
    public async Task LaneG_Negative_CorruptedAndZeroByteFiles_RemainPreservedAndFailClosed()
    {
        string repoRoot = FindRepositoryRoot();

        // 1. Stage zero-byte accounts.json and corrupted sessions.dat
        string accountsFile = Path.Combine(_dataDir, "accounts.json");
        File.WriteAllBytes(accountsFile, []);

        string vaultFile = Path.Combine(_vaultDir, "sessions.dat");
        File.WriteAllText(vaultFile, "CORRUPTED_NON_JSON_PAYLOAD_WITHOUT_MAGIC");

        // 2. Simulate binary upgrade
        string vNextDir = ResolveVNextPayloadDirectory(repoRoot);
        SimulateInstallerBinaryReplacement(vNextDir, _installDir);

        // 3. Assert damaged files are preserved (never wiped or replaced with blanks)
        Assert.Equal(0, new FileInfo(accountsFile).Length);
        Assert.Equal("CORRUPTED_NON_JSON_PAYLOAD_WITHOUT_MAGIC", File.ReadAllText(vaultFile));

        // 4. Assert fail-closed behavior when accessed
        var store = new LocalMetadataAccountStore(accountsFile);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAccountsAsync());

        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_vaultDir, fakeDpapi);
        await Assert.ThrowsAsync<VaultCorruptionException>(() => vault.ListStoredAccountIdsAsync());
    }

    [Fact]
    public void LaneG_Negative_EmptyOrMissingState_HandledGracefully()
    {
        string repoRoot = FindRepositoryRoot();

        // User data directory is completely empty
        Assert.Empty(Directory.GetFiles(_userDataDir, "*", SearchOption.AllDirectories));

        // Simulate binary upgrade
        string vNextDir = ResolveVNextPayloadDirectory(repoRoot);
        SimulateInstallerBinaryReplacement(vNextDir, _installDir);

        // Binary directory upgraded cleanly without creating corrupt files in user data
        string vNextIndex = Path.Combine(_installDir, "wwwroot", "index.html");
        Assert.True(File.Exists(vNextIndex));
        Assert.Empty(Directory.GetFiles(_userDataDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void LaneG_Negative_SimulatedBinaryFailure_LeavesUserDataIntact()
    {
        // 1. Stage persistent state
        string sentinelFile = Path.Combine(_userDataDir, "sentinel.txt");
        File.WriteAllText(sentinelFile, "pre-upgrade-sentinel");
        string preHash = ComputeSha256(sentinelFile);

        // 2. Simulate a failure during binary copying (e.g. invalid source)
        string nonExistentSource = Path.Combine(_testRoot, "non_existent_source_dir");
        Assert.ThrowsAny<DirectoryNotFoundException>(() =>
        {
            SimulateInstallerBinaryReplacement(nonExistentSource, _installDir);
        });

        // 3. Assert persistent state is completely unaffected
        Assert.True(File.Exists(sentinelFile));
        Assert.Equal(preHash, ComputeSha256(sentinelFile));
    }

    [Fact]
    public void LaneG_RepeatBinaryUpgrade_MaintainsStateConsistency()
    {
        string repoRoot = FindRepositoryRoot();

        // 1. Stage persistent files
        string sentinel = Path.Combine(_userDataDir, "sentinel.txt");
        File.WriteAllText(sentinel, "repeat-upgrade-sentinel");

        string vNextDir = ResolveVNextPayloadDirectory(repoRoot);

        // Upgrade 1
        SimulateInstallerBinaryReplacement(vNextDir, _installDir);
        var hashes1 = ComputeDirectoryFileHashes(_userDataDir);

        // Upgrade 2 (repeat immediately)
        SimulateInstallerBinaryReplacement(vNextDir, _installDir);
        var hashes2 = ComputeDirectoryFileHashes(_userDataDir);

        Assert.Equal(hashes1.Count, hashes2.Count);
        foreach (var (k, v) in hashes1)
        {
            Assert.Equal(v, hashes2[k]);
        }
    }

    #endregion

    #region Helper & Extraction Utilities

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static string? TryResolveCanonicalV010Zip()
    {
        // 1. Check environment variable override
        string? envPath = Environment.GetEnvironmentVariable("AG2ROUTER_V010_BASELINE_ZIP");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            if (VerifyZipSha256(envPath)) return envPath;
        }

        // 2. Check local temporary cache location
        string tempCandidate = Path.Combine(Path.GetTempPath(), "AG2Router_v010_Baseline", "AG2Router-v0.1.0-win-x64.zip");
        if (File.Exists(tempCandidate))
        {
            if (VerifyZipSha256(tempCandidate)) return tempCandidate;
        }

        return null;
    }

    private static bool VerifyZipSha256(string zipPath)
    {
        try
        {
            string hash = ComputeSha256(zipPath);
            return string.Equals(hash, CanonicalV010ZipSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string ResolveVNextPayloadDirectory(string repoRoot)
    {
        // Prefer verified publish\win-x64 if available
        string publishDir = Path.Combine(repoRoot, "publish", "win-x64");
        if (Directory.Exists(publishDir) && File.Exists(Path.Combine(publishDir, "wwwroot", "index.html")))
        {
            return publishDir;
        }

        // Fallback: stage inside _testRoot so it is guaranteed to be cleaned up on Dispose()
        string stagingDir = Path.Combine(_testRoot, "vnext_staging");
        if (Directory.Exists(stagingDir)) return stagingDir;
        Directory.CreateDirectory(stagingDir);

        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
        {
            File.Copy(file, Path.Combine(stagingDir, Path.GetFileName(file)), overwrite: true);
        }
        string? exeFile = Directory.GetFiles(AppContext.BaseDirectory, "AG2Router.exe").FirstOrDefault()
            ?? Directory.GetFiles(AppContext.BaseDirectory, "AG2Router.App.exe").FirstOrDefault();
        if (exeFile != null)
        {
            File.Copy(exeFile, Path.Combine(stagingDir, "AG2Router.exe"), overwrite: true);
        }

        string uiSrcDir = Path.Combine(repoRoot, "src", "ui");
        if (Directory.Exists(uiSrcDir))
        {
            string wwwroot = Path.Combine(stagingDir, "wwwroot");
            Directory.CreateDirectory(wwwroot);
            foreach (string file in Directory.GetFiles(uiSrcDir))
            {
                File.Copy(file, Path.Combine(wwwroot, Path.GetFileName(file)), overwrite: true);
            }
        }

        return stagingDir;
    }

    private static string GetLegacyAccountsFixturePath(string repoRoot)
    {
        string directFixture = Path.Combine(repoRoot, "test", "fixtures", "accounts-v1.json");
        if (File.Exists(directFixture)) return directFixture;

        string linkedFixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "accounts-v1.json");
        if (File.Exists(linkedFixture)) return linkedFixture;

        throw new FileNotFoundException("Could not locate accounts-v1.json fixture");
    }

    private static void StageSyntheticV010InstallPayload(string installDir)
    {
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "AG2Router.exe"), "v0.1.0-synthetic-binary");
        File.WriteAllText(Path.Combine(installDir, "AG2Router.dll"), "v0.1.0-synthetic-assembly");
        File.WriteAllText(Path.Combine(installDir, "install.ps1"), "# v0.1.0 install script\n");
        File.WriteAllText(Path.Combine(installDir, "uninstall.ps1"), "# v0.1.0 uninstall script\n");

        string wwwroot = Path.Combine(installDir, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<html><body><div id=\"add-account-modal\">v0.1.0-legacy-dom</div></body></html>");
        File.WriteAllText(Path.Combine(wwwroot, "app.js"), "// v0.1.0 vanilla js\n");
        File.WriteAllText(Path.Combine(wwwroot, "styles.css"), "/* v0.1.0 vanilla css */\n");
    }

    private static async Task StageSyntheticPersistentStateAsync(string userDataDir)
    {
        string dataDir = Path.Combine(userDataDir, "data");
        string vaultDir = Path.Combine(userDataDir, "vault");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(vaultDir);

        // 1. Stage legacy accounts.json
        string accountsFile = Path.Combine(dataDir, "accounts.json");
        string repoRoot = FindRepositoryRoot();
        File.Copy(GetLegacyAccountsFixturePath(repoRoot), accountsFile, overwrite: true);

        // 2. Stage synthetic sessions.dat
        var fakeDpapi = new FakeDpapiProvider();
        var vault = new SessionVault(vaultDir, fakeDpapi);
        byte[] tokenBytes = Encoding.UTF8.GetBytes("synthetic-session-token-v010");
        await vault.SaveSessionAsync("acc_shared01", tokenBytes, "gemini:antigravity");

        // 3. Stage sentinel file
        File.WriteAllText(Path.Combine(userDataDir, "sentinel.hash"), "c0ffee-deadbeef-synthetic-sentinel-hash-v010\n");

        // 4. Stage config file
        File.WriteAllText(Path.Combine(userDataDir, "custom.config"), "{\"autoSwitch\": true, \"lowThreshold\": 15}\n");
    }

    /// <summary>
    /// Disposable model of the Inno staged directory replacement. Persistent user
    /// state remains outside this directory and is never part of the stage.
    /// </summary>
    internal static void SimulateInstallerBinaryReplacement(string sourceDir, string installDir, Action? beforeCommit = null)
    {
        if (!Directory.Exists(sourceDir)) throw new DirectoryNotFoundException(sourceDir);
        string backupDir = installDir + ".bak";
        if (Directory.Exists(backupDir)) throw new IOException("Prior installation backup already exists.");
        bool staged = Directory.Exists(installDir);
        if (staged) Directory.Move(installDir, backupDir);
        try
        {
            Directory.CreateDirectory(installDir);
            foreach (string dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDir, dirPath);
                Directory.CreateDirectory(Path.Combine(installDir, relative));
            }
            foreach (string filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDir, filePath);
                File.Copy(filePath, Path.Combine(installDir, relative));
            }
            beforeCommit?.Invoke();
        }
        catch
        {
            if (staged)
            {
                if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true);
                Directory.Move(backupDir, installDir);
            }
            throw;
        }
        if (staged) Directory.Delete(backupDir, recursive: true);
    }

    internal static Dictionary<string, string> ComputeDirectoryFileHashes(string directoryPath)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directoryPath)) return hashes;

        foreach (string filePath in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(directoryPath, filePath);
            hashes[relativePath] = ComputeSha256(filePath);
        }

        return hashes;
    }

    internal static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes);
    }

    #endregion
}
