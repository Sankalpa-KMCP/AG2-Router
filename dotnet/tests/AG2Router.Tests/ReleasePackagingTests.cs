using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using AG2Router.Windows.Lifecycle;
using Xunit;

namespace AG2Router.Tests;

public class ReleasePackagingTests
{
    private readonly SingleInstanceIpcNamespace _ipcNamespace = SingleInstanceTestIpc.CreateNamespace();

    private const string ExpectedInstallDir = @"C:\Users\testuser\AppData\Local\Programs\AG2Router";
    private const string ExpectedExe = @"C:\Users\testuser\AppData\Local\Programs\AG2Router\AG2Router.exe";
    private const string ProtectedUserDataDir = @"C:\Users\testuser\AppData\Local\AG2-Router";

    #region Startup Run-Value Ownership Extraction & Regression Tests (DEF-01, DEF-05)

    /// <summary>
    /// Implements the exact parsing logic used in scripts/uninstall.ps1 and installer/AG2Router.iss.
    /// Extracts the target executable path from a Windows registry command line.
    /// </summary>
    public static string? ExtractStartupExecutablePath(string? runVal)
    {
        if (string.IsNullOrWhiteSpace(runVal))
            return null;

        string trimmed = runVal.Trim();
        // Canonical quoted format: "C:\path\AG2Router.exe" --tray
        var quotedMatch = Regex.Match(
            trimmed,
            @"^""(?<exe>[^""]+)""(?:\s+.*)?$",
            RegexOptions.CultureInvariant);
        if (quotedMatch.Success)
            return quotedMatch.Groups["exe"].Value.Trim();

        // Unquoted format: C:\path\AG2Router.exe --tray
        var unquotedMatch = Regex.Match(
            trimmed,
            @"^(?<exe>.*?\.exe)(?:\s+.*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (unquotedMatch.Success)
            return unquotedMatch.Groups["exe"].Value.Trim();

        return trimmed;
    }

    /// <summary>
    /// Validates whether a registry Run value is positively proven to belong to the installed AG2Router executable.
    /// </summary>
    public static bool IsStartupRegistrationOwned(string? runVal, string expectedExe)
    {
        string? extracted = ExtractStartupExecutablePath(runVal);
        if (string.IsNullOrWhiteSpace(extracted))
            return false;

        try
        {
            string normalizedRun = Path.GetFullPath(extracted);
            string normalizedExpected = Path.GetFullPath(expectedExe);
            return string.Equals(normalizedRun, normalizedExpected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Malformed paths with illegal characters fail closed (return false)
            return false;
        }
    }

    [Theory]
    [InlineData(@"""C:\Users\testuser\AppData\Local\Programs\AG2Router\AG2Router.exe"" --tray", true)]
    [InlineData(@"""C:\Users\testuser\AppData\Local\Programs\AG2Router\AG2Router.exe""", true)]
    [InlineData(@"C:\Users\testuser\AppData\Local\Programs\AG2Router\AG2Router.exe --tray", true)]
    [InlineData(@"C:\Users\testuser\AppData\Local\Programs\AG2Router\AG2Router.exe", true)]
    public void StartupOwnership_OwnedCommands_ArePositivelyRecognized(string commandLine, bool expectedOwned)
    {
        bool isOwned = IsStartupRegistrationOwned(commandLine, ExpectedExe);
        Assert.Equal(expectedOwned, isOwned);
    }

    [Theory]
    [InlineData(@"""D:\AG2-Router\bin\Debug\AG2Router.exe"" --tray")]
    [InlineData(@"""D:\AG2-Router\publish\win-x64\AG2Router.exe"" --tray")]
    [InlineData(@"""C:\Program Files\OtherApp\OtherApp.exe"" --tray")]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    public void StartupOwnership_ForeignOrDeveloperCommands_ArePreserved(string foreignCommandLine)
    {
        bool isOwned = IsStartupRegistrationOwned(foreignCommandLine, ExpectedExe);
        Assert.False(isOwned, $"Foreign command line '{foreignCommandLine}' must NOT be identified as owned.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("malformed string without exe")]
    [InlineData(@"""unclosed quote")]
    [InlineData(@"""C:\invalid\bad""quote""path.exe"" --tray")]
    [InlineData(@"C:\invalid*characters?in\path.exe --tray")]
    public void StartupOwnership_MalformedCommands_FailClosedWithoutException(string? malformedCommandLine)
    {
        bool isOwned = IsStartupRegistrationOwned(malformedCommandLine, ExpectedExe);
        Assert.False(isOwned, "Malformed command line must fail closed and never report ownership.");
    }

    #endregion

    #region Path Isolation & User Data Preservation (SAFE-01 .. SAFE-04)

    [Fact]
    public void PathIsolation_ProgramFilesVsPersistentUserData_AreStrictlyDisjoint()
    {
        Assert.NotEqual(ExpectedInstallDir, ProtectedUserDataDir);
        Assert.False(
            ExpectedInstallDir.StartsWith(ProtectedUserDataDir, StringComparison.OrdinalIgnoreCase),
            "InstallDir must not be inside user data"
        );
        Assert.False(
            ProtectedUserDataDir.StartsWith(ExpectedInstallDir, StringComparison.OrdinalIgnoreCase),
            "User data must not be inside InstallDir"
        );
    }

    #endregion

    #region Bounded --exit Shutdown Completion Tests (DEF-02, DEF-03)

    [Fact]
    public async Task SingleInstanceGuard_RequestExitAndWait_WhenPrimaryRunning_WaitsAndConfirmsExit()
    {
        var exitReceivedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var guardAcquiredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var guard = new SingleInstanceGuard(_ipcNamespace);

        // Dedicated thread ensures Mutex is acquired and released on the exact same thread
        var primaryThread = new Thread(() =>
        {
            bool acquired = guard.TryAcquire(
                onActivateRequested: () => { },
                onCloseRequested: null,
                onExitRequested: () =>
                {
                    exitReceivedTcs.TrySetResult(true);
                }
            );

            guardAcquiredTcs.TrySetResult(acquired);

            // Wait until exit command is dispatched
            exitReceivedTcs.Task.Wait(TimeSpan.FromSeconds(3));

            // Cleanly dispose guard on the same thread that acquired it
            guard.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        });

        primaryThread.IsBackground = true;
        primaryThread.Start();

        bool isAcquired = await guardAcquiredTcs.Task;
        Assert.True(isAcquired, "Primary instance must acquire single-instance guard");

        try
        {
            // Bounded wait: helper sends EXIT and confirms primary released mutex
            bool exited = SingleInstanceGuard.RequestExitAndWait(_ipcNamespace, timeoutMs: 3000);
            Assert.True(exited, "RequestExitAndWait must confirm primary instance termination");
            Assert.True(await exitReceivedTcs.Task, "Primary instance must receive EXIT command");
        }
        finally
        {
            primaryThread.Join(1000);
        }
    }

    [Fact]
    public void SingleInstanceGuard_RequestExitAndWait_WhenPrimaryNotRunning_ReturnsTrueImmediately()
    {
        // When no primary instance is running, RequestExitAndWait should complete immediately with true
        var sw = Stopwatch.StartNew();
        bool exited = SingleInstanceGuard.RequestExitAndWait(_ipcNamespace, timeoutMs: 500);
        sw.Stop();

        Assert.True(exited, "Must report true when primary is already not running");
        Assert.True(sw.ElapsedMilliseconds < 1000, "Must return promptly without hanging");
    }

    [Fact]
    public async Task SingleInstanceGuard_RequestExitAndWait_WhenPrimaryHangs_TimesOutAndReturnsFalse()
    {
        var guard = new SingleInstanceGuard(_ipcNamespace);
        // Acquire on a background thread so the test thread does not have mutex ownership
        bool acquired = await Task.Run(() => guard.TryAcquire(
            onActivateRequested: () => { },
            onCloseRequested: null,
            onExitRequested: () =>
            {
                // Deliberately do NOT dispose guard to simulate hung primary process
            }
        ));

        Assert.True(acquired);

        try
        {
            // Short timeout to verify bounded failure
            bool exited = SingleInstanceGuard.RequestExitAndWait(_ipcNamespace, timeoutMs: 300);
            Assert.False(exited, "RequestExitAndWait must return false when primary fails to exit within timeout");
        }
        finally
        {
            await guard.DisposeAsync();
        }
    }

    #endregion

    #region Inno Setup Non-Admin & Safety Contract Consistency (DEF-02, DEF-04)

    [Fact]
    public void InnoSetupDefinition_EnforcesPerUserNonAdminAndNoDialogOverride()
    {
        // Dynamically resolve repository root by seeking package.json upwards
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json")))
        {
            dir = dir.Parent;
        }

        string repoRoot = dir?.FullName ?? Directory.GetCurrentDirectory();
        string issPath = Path.Combine(repoRoot, "installer", "AG2Router.iss");

        Assert.True(File.Exists(issPath), $"installer/AG2Router.iss must exist at {issPath}");

        string issContent = File.ReadAllText(issPath);

        // Verify per-user configuration
        Assert.Contains("PrivilegesRequired=lowest", issContent);
        Assert.DoesNotContain("PrivilegesRequiredOverridesAllowed=dialog", issContent);
        Assert.Contains("PrivilegesRequiredOverridesAllowed=commandline", issContent);

        // Verify admin lockout in InitializeSetup
        Assert.Contains("IsAdminInstallMode", issContent);

        // Verify pre-install graceful shutdown check in PrepareToInstall
        Assert.Contains("function PrepareToInstall", issContent);
        Assert.Contains("'--exit'", issContent);

        // Verify uninstaller graceful shutdown via InitializeUninstall
        Assert.Contains("function InitializeUninstall(): Boolean", issContent);
        Assert.Contains("'--exit'", issContent);
        Assert.DoesNotContain("[UninstallRun]", issContent);
    }

    #endregion

    #region Canonical Release Pipeline Contract

    [Fact]
    public void ReleaseWorkflow_RequiresPinnedCompilerAndAllCanonicalArtifacts()
    {
        string repoRoot = FindRepositoryRoot();
        string workflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "release.yml"));
        string provisioner = File.ReadAllText(Path.Combine(repoRoot, "scripts", "provision-inno.ps1"));

        Assert.Contains("./scripts/provision-inno.ps1 -PinnedVersion 6.4.0", workflow);
        Assert.Contains("-RequireInstaller", workflow);
        Assert.Contains("-IsccPath '${{ steps.inno.outputs.iscc_path }}'", workflow);
        Assert.Contains("Assert Required Release Outputs", workflow);
        Assert.Contains("dist/AG2Router-v${{ steps.version.outputs.version }}-win-x64.zip", workflow);
        Assert.Contains("dist/AG2Router-Setup-v${{ steps.version.outputs.version }}-win-x64.exe", workflow);
        Assert.DoesNotContain("VersionInfo", provisioner);
        Assert.Contains("list --exact innosetup --limit-output", provisioner);
        Assert.Contains("--allow-downgrade --force --install-if-not-installed", provisioner);
        Assert.Contains("Compiler engine version: Inno Setup $PinnedVersion", provisioner);
    }

    [Theory]
    [InlineData("0.4.0", "v0.4.0", true)]
    [InlineData("0.4.0", "v0.4.1", false)]
    [InlineData("0.4.0", "V0.4.0", false)]
    [InlineData("0.4.0", "v0.4.0-beta", false)]
    public void ReleaseTagVersionGuard_AcceptsOnlyTheCanonicalTag(
        string canonicalVersion,
        string tagName,
        bool expectedAccepted)
    {
        Assert.Equal(expectedAccepted, IsCanonicalReleaseTag(canonicalVersion, tagName));
    }

    [Fact]
    public void ReleaseWorkflow_TagVersionGuardUsesCanonicalVersionAndRunsBeforePackaging()
    {
        string repoRoot = FindRepositoryRoot();
        string workflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "release.yml"));

        Assert.Contains("[xml]$props = Get-Content \"dotnet/Directory.Build.props\"", workflow);
        Assert.Contains("$canonicalVersion = $props.Project.PropertyGroup.Version", workflow);
        Assert.Contains("\"version=$canonicalVersion\" | Out-File -FilePath $env:GITHUB_OUTPUT", workflow);
        Assert.Contains("if: github.event_name == 'push' && github.ref_type == 'tag'", workflow);
        Assert.Contains("$expectedTag = \"v${{ steps.version.outputs.version }}\"", workflow);
        Assert.Contains("$actualTag = \"${{ github.ref_name }}\"", workflow);
        Assert.Contains("if ($actualTag -cne $expectedTag)", workflow);
        Assert.Contains("Release tag/version mismatch", workflow);

        int guardPosition = workflow.IndexOf("Verify Release Tag Matches Canonical Version", StringComparison.Ordinal);
        int packagingPosition = workflow.IndexOf("Run Release Packaging Procedure", StringComparison.Ordinal);
        Assert.True(guardPosition >= 0 && guardPosition < packagingPosition,
            "The tag/version guard must run before release packaging.");

        Assert.Contains("AG2Router-v${{ steps.version.outputs.version }}-win-x64", workflow);
        Assert.DoesNotContain("-Version ${{ github.ref_name }}", workflow);
    }

    [Fact]
    public void WindowsCi_CompilesInstallerWithPinnedCompilerBeforeTagging()
    {
        string repoRoot = FindRepositoryRoot();
        string workflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "dotnet-ci.yml"));

        Assert.Contains("./scripts/provision-inno.ps1 -PinnedVersion 6.4.0", workflow);
        Assert.Contains("& '${{ steps.inno.outputs.iscc_path }}' /O-", workflow);
        Assert.Contains("/DSourceDir=$source", workflow);
        Assert.Contains("installer/AG2Router.iss", workflow);
        Assert.Contains("if ($LASTEXITCODE -ne 0)", workflow);
    }

    [Fact]
    public void PackageRelease_InstallerRequiredModeFailsClosed()
    {
        string repoRoot = FindRepositoryRoot();
        string packageScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "package-release.ps1"));

        Assert.Contains("[switch]$RequireInstaller", packageScript);
        Assert.Contains("[string]$IsccPath", packageScript);
        Assert.Contains("Installer compilation is required", packageScript);
        Assert.Contains("ISCC reported success, but the required installer was not produced", packageScript);
        Assert.Contains("Required packaging output is missing", packageScript);
    }

    [Fact]
    public void CanonicalVersion_AuthorityAndFallbacks_AreStrictlyAligned()
    {
        string repoRoot = FindRepositoryRoot();

        // 1. Primary Authority: dotnet/Directory.Build.props
        string propsPath = Path.Combine(repoRoot, "dotnet", "Directory.Build.props");
        Assert.True(File.Exists(propsPath), "Directory.Build.props must exist as primary version authority");
        string propsContent = File.ReadAllText(propsPath);

        var versionMatch = Regex.Match(propsContent, @"<Version>(?<ver>[^<]+)</Version>");
        Assert.True(versionMatch.Success, "Directory.Build.props must specify <Version>");
        string canonicalVersion = versionMatch.Groups["ver"].Value.Trim();
        Assert.Matches(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", canonicalVersion);
        string numericVersion = canonicalVersion.Split('-')[0];

        // Assembly/File versions are numeric; informational version retains prerelease identity.
        Assert.Contains($"<AssemblyVersion>{numericVersion}</AssemblyVersion>", propsContent);
        Assert.Contains($"<FileVersion>{numericVersion}</FileVersion>", propsContent);
        Assert.Contains($"<InformationalVersion>{canonicalVersion}</InformationalVersion>", propsContent);

        // 2. package.json and package-lock.json
        string packageJson = File.ReadAllText(Path.Combine(repoRoot, "package.json"));
        Assert.Contains($"\"version\": \"{canonicalVersion}\"", packageJson);

        string packageLockJson = File.ReadAllText(Path.Combine(repoRoot, "package-lock.json"));
        Assert.Contains($"\"version\": \"{canonicalVersion}\"", packageLockJson);

        using (var pkgDoc = System.Text.Json.JsonDocument.Parse(packageJson))
        using (var lockDoc = System.Text.Json.JsonDocument.Parse(packageLockJson))
        {
            string pkgNodeEngine = pkgDoc.RootElement.GetProperty("engines").GetProperty("node").GetString()!;
            string lockNodeEngine = lockDoc.RootElement.GetProperty("packages").GetProperty("").GetProperty("engines").GetProperty("node").GetString()!;
            Assert.Equal(pkgNodeEngine, lockNodeEngine);
            Assert.DoesNotContain(">=20.0.0", pkgNodeEngine);
            Assert.Contains("20.19", pkgNodeEngine);
            Assert.Contains("22.12", pkgNodeEngine);
        }

        // 3. installer/AG2Router.iss fallback literal
        string issContent = File.ReadAllText(Path.Combine(repoRoot, "installer", "AG2Router.iss"));
        Assert.Contains($"#define AppVersion \"{canonicalVersion}\"", issContent);

        // 4. scripts/package-release.ps1 derives canonical version and fails closed without fallback
        string packageScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "package-release.ps1"));
        Assert.Contains("Could not derive canonical version from $PropsPath. Pass -Version explicitly.", packageScript);
        Assert.DoesNotContain($"$Version = \"{canonicalVersion}\"", packageScript);

        // 5. scripts/install.ps1 fallback literal and dynamic derivation
        string installScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "install.ps1"));
        Assert.Contains($"$DisplayVersion = \"{canonicalVersion}\"", installScript);
        Assert.Contains("FileVersionInfo]::GetVersionInfo", installScript);

        // 6. User-facing documentation consistency
        string readme = File.ReadAllText(Path.Combine(repoRoot, "README.md"));
        Assert.Contains($"AG2Router-v{canonicalVersion}-win-x64.zip", readme);
        Assert.Contains($"AG2Router-Setup-v{canonicalVersion}-win-x64.exe", readme);

        string releaseNotesPath = Path.Combine(repoRoot, "docs", $"release-notes-v{canonicalVersion}.md");
        Assert.True(File.Exists(releaseNotesPath), $"Release notes for v{canonicalVersion} must exist at {releaseNotesPath}");
        string releaseNotes = File.ReadAllText(releaseNotesPath);
        Assert.Contains($"**Version:** {canonicalVersion}", releaseNotes);
    }

    [Fact]
    public void PackageRelease_ArchiveStep_DoesNotContainDeadVariables()
    {
        string repoRoot = FindRepositoryRoot();
        string packageScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "package-release.ps1"));

        Assert.DoesNotContain("$FilesToZip", packageScript);
        Assert.Contains("Compress-Archive -Path (Join-Path $PublishDir \"*\")", packageScript);
    }

    [Fact]
    public void Workflows_ExplicitMinimalPermissions_AreConfiguredAcrossAllWorkflows()
    {
        string repoRoot = FindRepositoryRoot();
        string[] workflowFiles = ["ci.yml", "dotnet-ci.yml", "release.yml"];

        foreach (string file in workflowFiles)
        {
            string path = Path.Combine(repoRoot, ".github", "workflows", file);
            string content = File.ReadAllText(path);

            Assert.Contains("permissions:", content);
            Assert.Contains("contents: read", content);
        }
    }

    [Fact]
    public void Workflows_JobTimeoutsAndConcurrency_AreConfiguredAcrossAllWorkflows()
    {
        string repoRoot = FindRepositoryRoot();

        // 1. ci.yml: concurrency with cancel-in-progress, timeout-minutes: 15
        string ci = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "ci.yml"));
        Assert.Contains("concurrency:", ci);
        Assert.Contains("cancel-in-progress: true", ci);
        Assert.Contains("timeout-minutes: 15", ci);

        // 2. dotnet-ci.yml: concurrency with cancel-in-progress, timeout-minutes: 15, no stale branches
        string dotnetCi = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "dotnet-ci.yml"));
        Assert.Contains("concurrency:", dotnetCi);
        Assert.Contains("cancel-in-progress: true", dotnetCi);
        Assert.Contains("timeout-minutes: 15", dotnetCi);
        Assert.DoesNotContain("feat/dotnet-native-shell", dotnetCi);

        // 3. release.yml: non-cancelling concurrency, timeout-minutes: 30
        string release = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "release.yml"));
        Assert.Contains("concurrency:", release);
        Assert.Contains("cancel-in-progress: false", release);
        Assert.Contains("timeout-minutes: 30", release);
    }

    [Fact]
    public void Workflows_ActionVersions_ArePinnedToImmutableCommitShas()
    {
        string repoRoot = FindRepositoryRoot();
        string[] workflowFiles = ["ci.yml", "dotnet-ci.yml", "release.yml"];
        var usesRegex = new Regex(@"uses:\s+(?<action>[a-zA-Z0-9_\-\.\/]+)@(?<ref>[^\s#]+)", RegexOptions.Compiled);

        foreach (string file in workflowFiles)
        {
            string path = Path.Combine(repoRoot, ".github", "workflows", file);
            string content = File.ReadAllText(path);

            var matches = usesRegex.Matches(content);
            Assert.NotEmpty(matches);

            foreach (Match match in matches)
            {
                string action = match.Groups["action"].Value;
                string actionRef = match.Groups["ref"].Value;

                // Action refs must be 40-character hex commit SHAs
                Assert.Matches("^[0-9a-f]{40}$", actionRef);
                Assert.False(actionRef.StartsWith("v", StringComparison.OrdinalIgnoreCase),
                    $"Action {action} in {file} uses mutable tag '{actionRef}' instead of immutable SHA.");
            }
        }
    }

    #region Inno Setup Upgrade & Safety Semantics (vNext)

    [Fact]
    public void InnoSetup_AppIdAndUpgradeDirectives_AreEnforced()
    {
        string repoRoot = FindRepositoryRoot();
        string issContent = File.ReadAllText(Path.Combine(repoRoot, "installer", "AG2Router.iss"));

        // 1. Stable AppId invariant
        Assert.Contains("AppId={{D37E7404-585A-4B6A-B7F9-5360980DF628}", issContent);

        // 2. Default directory and seamless upgrade directives
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\AG2Router", issContent);
        Assert.Contains("DisableDirPage=auto", issContent);
        Assert.Contains("CloseApplications=no", issContent);
        Assert.Contains("PrivilegesRequired=lowest", issContent);
    }

    [Fact]
    public void InnoSetup_NeverTouchesUserDataOrCredentialManager()
    {
        string repoRoot = FindRepositoryRoot();
        string issContent = File.ReadAllText(Path.Combine(repoRoot, "installer", "AG2Router.iss"));

        // Inno Setup must never touch %LOCALAPPDATA%\AG2-Router or gemini:antigravity
        Assert.DoesNotContain("AG2-Router", issContent);
        Assert.DoesNotContain("gemini:antigravity", issContent);
        Assert.DoesNotContain("[UninstallDelete]", issContent);
    }

    [Theory]
    // Positive match: exact legacy script registration properties
    [InlineData("AG2 Router", "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router", @"powershell.exe -File ""C:\Users\test\AppData\Local\Programs\AG2Router\uninstall.ps1""", @"C:\Users\test\AppData\Local\Programs\AG2Router", true)]
    [InlineData("ag2 router", "ag2", @"C:\Users\test\AppData\Local\Programs\AG2Router\", @"powershell.exe -File C:\Users\test\AppData\Local\Programs\AG2Router\uninstall.ps1", @"C:\Users\test\AppData\Local\Programs\AG2Router", true)]
    // Negative guards: foreign name, publisher, mismatched directory, or foreign uninstall string
    [InlineData("Other App", "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router", @"powershell.exe -File uninstall.ps1", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    [InlineData("AG2 Router", "Other Corp", @"C:\Users\test\AppData\Local\Programs\AG2Router", @"powershell.exe -File uninstall.ps1", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    [InlineData("AG2 Router", "AG2", @"C:\Program Files\OtherApp", @"powershell.exe -File uninstall.ps1", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    [InlineData("AG2 Router", "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router", @"cmd.exe /c del /f", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    [InlineData(null, "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router", @"powershell.exe", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    public void LegacyScriptUninstall_StrictGuards_RecognizeOnlyOwnedRegistrations(
        string? displayName,
        string? publisher,
        string? installLocation,
        string? uninstallString,
        string expectedAppDir,
        bool expectedResult)
    {
        bool isOwned = IsLegacyScriptUninstallOwned(displayName, publisher, installLocation, uninstallString, expectedAppDir);
        Assert.Equal(expectedResult, isOwned);
    }

    /// <summary>
    /// Mirrors the exact 5-guard Pascal verification logic in Inno Setup.
    /// </summary>
    public static bool IsLegacyScriptUninstallOwned(
        string? displayName,
        string? publisher,
        string? installLocation,
        string? uninstallString,
        string expectedAppDir)
    {
        if (string.IsNullOrWhiteSpace(displayName) || !string.Equals(displayName.Trim(), "AG2 Router", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(publisher) || !string.Equals(publisher.Trim(), "AG2", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(installLocation))
            return false;

        string cleanInstallLocation = installLocation.Trim().TrimEnd('\\');
        string cleanExpectedAppDir = expectedAppDir.Trim().TrimEnd('\\');
        if (!string.Equals(cleanInstallLocation, cleanExpectedAppDir, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(uninstallString))
            return false;
        string lowerUninstall = uninstallString.ToLowerInvariant();
        if (!lowerUninstall.Contains("uninstall.ps1") && !lowerUninstall.Contains("ag2router"))
            return false;

        return true;
    }

    [Fact]
    public void InnoSetup_PrunesLegacyUninstallKeyDuringPostInstall()
    {
        string repoRoot = FindRepositoryRoot();
        string issContent = File.ReadAllText(Path.Combine(repoRoot, "installer", "AG2Router.iss"));

        Assert.Contains("procedure CurStepChanged", issContent);
        Assert.Contains("CurStep = ssDone", issContent);
        Assert.Contains(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AG2Router", issContent);
        Assert.Contains("RegDeleteKeyIncludingSubkeys", issContent);
    }

    [Fact]
    public void StartupRunKey_IsNeverDisturbedDuringUpgrade()
    {
        string repoRoot = FindRepositoryRoot();
        string issContent = File.ReadAllText(Path.Combine(repoRoot, "installer", "AG2Router.iss"));

        // Ensure no [Registry] directive touches the Run key
        Assert.DoesNotContain("[Registry]", issContent);

        // Verify RegDeleteValue on Run key only occurs in CurUninstallStepChanged (usUninstall)
        Assert.Contains("CurUninstallStep = usUninstall", issContent);
        int runKeyDeletePos = issContent.IndexOf(@"RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run'", StringComparison.Ordinal);
        Assert.True(runKeyDeletePos > 0);
        string deleteSection = issContent.Substring(runKeyDeletePos);
        Assert.DoesNotContain("CurStepChanged", deleteSection);
    }

    [Fact]
    public void InnoSetup_And_InstallScripts_CoordinateWithSessionMutexAndFailClosed()
    {
        string repoRoot = FindRepositoryRoot();
        string issContent = File.ReadAllText(Path.Combine(repoRoot, "installer", "AG2Router.iss"));
        string installScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "install.ps1"));
        string uninstallScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "uninstall.ps1"));

        // 1. Inno Setup mutex imports and coordination
        Assert.Contains("OpenMutexW", issContent);
        Assert.Contains("CloseHandle", issContent);
        Assert.Contains(@"Local\AG2Router_Session_Mutex", issContent);
        Assert.Contains("function SessionMutexState(): Integer", issContent);
        Assert.Contains("function WaitForSessionMutexRelease", issContent);
        Assert.Contains("if SessionMutexState() < 0 then", issContent);
        Assert.Contains("DLLGetLastError = ERROR_FILE_NOT_FOUND", issContent);
        Assert.DoesNotContain("ewWaitUntilTerminated", issContent);
        Assert.Contains("ewNoWait", issContent);

        // 2. Inno Setup PrepareToInstall & InitializeUninstall fail-closed guards
        Assert.Contains("function PrepareToInstall", issContent);
        Assert.Contains("function InitializeUninstall(): Boolean", issContent);

        // 3. install.ps1 mutex check and fail-closed guards
        Assert.Contains(@"Local\AG2Router_Session_Mutex", installScript);
        Assert.Contains("[System.Threading.Mutex]::TryOpenExisting", installScript);
        Assert.Contains("Installation aborted", installScript);

        // 4. uninstall.ps1 mutex check and fail-closed guards
        Assert.Contains(@"Local\AG2Router_Session_Mutex", uninstallScript);
        Assert.Contains("[System.Threading.Mutex]::TryOpenExisting", uninstallScript);
        Assert.Contains("Uninstallation aborted", uninstallScript);
    }

    [Fact]
    public void InnoSetup_ImplementsAtomicStagedUpgradeWithRollback()
    {
        string repoRoot = FindRepositoryRoot();
        string issContent = File.ReadAllText(Path.Combine(repoRoot, "installer", "AG2Router.iss"));

        // 1. Win32 MoveFileW import for atomic directory moves
        Assert.Contains("MoveFileW", issContent);

        // 2. Staging occurs in the abortable pre-extraction event.
        string preparation = issContent.Split("function PrepareToInstall(var NeedsRestart: Boolean): String;")[1]
            .Split("procedure CurStepChanged(CurStep: TSetupStep);")[0];
        Assert.Contains("if DirExists(BackupDir) then", preparation);
        Assert.Contains("if not MoveFileW(AppDir, BackupDir) then", preparation);
        Assert.Contains("No application files were overwritten", preparation);
        Assert.Contains(".bak", issContent);
        Assert.Contains("HasBackup := True", issContent);

        // 3. Backup is retained until the successful terminal event.
        Assert.Contains("CurStep = ssDone", issContent);
        Assert.Contains("InstallCompleted := True", issContent);
        Assert.Contains("not DelTree(BackupDir, True, True, True)", issContent);

        // 4. DeinitializeSetup restores backup if install was incomplete
        Assert.Contains("procedure DeinitializeSetup()", issContent);
        Assert.Contains("HasBackup and (not InstallCompleted)", issContent);
        Assert.Contains("MoveFileW(BackupDir, AppDir)", issContent);
        Assert.Contains("not DelTree(AppDir, True, True, True)", issContent);
        Assert.Contains("if DirExists(BackupDir) then", issContent);
        Assert.Contains("if not MoveFileW(BackupDir, AppDir) then", issContent);
        Assert.DoesNotContain("DirExists(BackupDir) and (not MoveFileW", issContent);
        Assert.Contains("remains in the sibling .bak directory", issContent);
    }

    [Fact]
    public void InnoUninstall_UnknownMutexAndHungExitFailClosedBeforeRemoval()
    {
        string issContent = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "AG2Router.iss"));
        string uninstall = issContent.Split("function InitializeUninstall(): Boolean;")[1]
            .Split("// Helper function to strip trailing backslashes")[0];
        Assert.Contains("if SessionMutexState() < 0 then", uninstall);
        Assert.Contains("if not FileExists(InstalledExe) then", uninstall);
        Assert.Contains("ewNoWait", uninstall);
        Assert.Contains("if not WaitForSessionMutexRelease(5000) then", uninstall);
        Assert.DoesNotContain("ewWaitUntilTerminated", uninstall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PowerShellUninstall_ProcessQueryFailureAbortsBeforeDestructiveContinuation(bool failAfterExit)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        string sequence = failAfterExit
            ? "$global:AG2Synthetic.ProcessStates.Enqueue('RUNNING'); $global:AG2Synthetic.ProcessStates.Enqueue('UNKNOWN')"
            : "$global:AG2Synthetic.ProcessState = 'UNKNOWN'";
        using var result = await environment.RunAsync("uninstall",
            SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry + "\n" + sequence);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(failAfterExit ? "became unknown" : "could not be verified", result.Output);
        Assert.Equal(failAfterExit ? 1 : 0, result.State.GetProperty("ExitRequests").GetInt32());
        Assert.Equal(failAfterExit ? 2 : 1, result.Operations.Count(x => x.StartsWith("ProcessState:")));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("RemoveRegistry") || x.StartsWith("FileSystem/Remove-Item"));
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.True(result.State.GetProperty("Registry").TryGetProperty(SyntheticInstallationTestEnvironment.UninstallKey, out _));
        AssertSyntheticBoundary(environment, result);
    }

    #endregion

    #region Installation Ownership & Cross-Channel Invariants (F04)

    public const string CanonicalInnoAppId = "{D37E7404-585A-4B6A-B7F9-5360980DF628}";
    public const string CanonicalInnoKeyName = "{D37E7404-585A-4B6A-B7F9-5360980DF628}_is1";
    public const string CanonicalLegacyKeyName = "AG2Router";

    /// <summary>
    /// Validates whether an Inno Setup uninstall registration belongs to this AG2 Router installation.
    /// </summary>
    public static bool IsInnoUninstallRegistrationOwned(
        string? displayName,
        string? publisher,
        string? installLocation,
        string? appPath,
        string? uninstallString,
        string expectedAppDir)
    {
        if (string.IsNullOrWhiteSpace(displayName) ||
            !Regex.IsMatch(displayName.Trim(), @"^AG2\s+Router", RegexOptions.IgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(publisher) &&
            !string.Equals(publisher.Trim(), "AG2", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string cleanExpected = Path.GetFullPath(expectedAppDir).TrimEnd('\\');

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            try
            {
                string cleanInstall = Path.GetFullPath(installLocation).TrimEnd('\\');
                if (string.Equals(cleanInstall, cleanExpected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(appPath))
        {
            try
            {
                string cleanApp = Path.GetFullPath(appPath).TrimEnd('\\');
                if (string.Equals(cleanApp, cleanExpected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(uninstallString))
        {
            try
            {
                string trimmed = uninstallString.Trim();
                string? exe = null;
                var quoted = Regex.Match(trimmed, @"^""(?<exe>[^""]+)""");
                if (quoted.Success)
                    exe = quoted.Groups["exe"].Value;
                else
                {
                    var unquoted = Regex.Match(trimmed, @"^(?<exe>\S+\.exe)");
                    if (unquoted.Success)
                        exe = unquoted.Groups["exe"].Value;
                }

                if (!string.IsNullOrWhiteSpace(exe))
                {
                    string? dir = Path.GetDirectoryName(Path.GetFullPath(exe))?.TrimEnd('\\');
                    if (string.Equals(dir, cleanExpected, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
        }

        return false;
    }

    [Theory]
    // Positive matches: exact properties or valid paths
    [InlineData("AG2 Router", "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router", null, null, @"C:\Users\test\AppData\Local\Programs\AG2Router", true)]
    [InlineData("AG2 Router version 0.4.0", "AG2", null, @"C:\Users\test\AppData\Local\Programs\AG2Router", null, @"C:\Users\test\AppData\Local\Programs\AG2Router", true)]
    [InlineData("AG2 Router", "AG2", null, null, @"""C:\Users\test\AppData\Local\Programs\AG2Router\unins000.exe""", @"C:\Users\test\AppData\Local\Programs\AG2Router", true)]
    [InlineData("AG2 Router", "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router\", null, null, @"C:\Users\test\AppData\Local\Programs\AG2Router", true)]
    // Negative guards: foreign app name, foreign publisher, wrong path
    [InlineData("Other App", "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router", null, null, @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    [InlineData("AG2 Router", "Other Corp", @"C:\Program Files\OtherApp", null, null, @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    [InlineData("AG2 Router", "AG2", @"C:\Program Files\OtherApp", null, null, @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    [InlineData(null, null, @"C:\Users\test\AppData\Local\Programs\AG2Router", null, null, @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    public void InnoUninstallRegistration_StrictGuards_RecognizeOnlyOwnedRegistrations(
        string? displayName,
        string? publisher,
        string? installLocation,
        string? appPath,
        string? uninstallString,
        string expectedAppDir,
        bool expectedResult)
    {
        bool isOwned = IsInnoUninstallRegistrationOwned(displayName, publisher, installLocation, appPath, uninstallString, expectedAppDir);
        Assert.Equal(expectedResult, isOwned);
    }

    [Fact]
    public void InstallationOwnership_Contracts_AreSynchronizedAcrossInnoAndScripts()
    {
        string repoRoot = FindRepositoryRoot();
        string issContent = File.ReadAllText(Path.Combine(repoRoot, "installer", "AG2Router.iss"));
        string installScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "install.ps1"));
        string uninstallScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "uninstall.ps1"));

        // AppId alignment
        Assert.Contains("AppId={" + CanonicalInnoAppId, issContent);
        Assert.Contains(CanonicalInnoAppId, installScript);
        Assert.Contains(CanonicalInnoAppId, uninstallScript);

        // Preflight ownership checks presence
        Assert.Contains("Get-AG2RouterInstallationOwnership", installScript);
        Assert.Contains("Get-AG2RouterInstallationOwnership", uninstallScript);
        Assert.Contains("Installation Channel Conflict", installScript);
        Assert.Contains("Installation Channel Conflict", uninstallScript);
        Assert.Contains("InnoOwned", installScript);
        Assert.Contains("InnoOwned", uninstallScript);
        Assert.Contains("Ambiguous", installScript);
        Assert.Contains("Ambiguous", uninstallScript);
        Assert.Contains("ForeignConflict", installScript);
        Assert.Contains("ForeignConflict", uninstallScript);
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellUninstall_SucceedsForPowerShellOwned()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        using var result = await environment.RunAsync("uninstall", SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(Directory.Exists(environment.Target));
        Assert.False(result.State.GetProperty("Registry").TryGetProperty(SyntheticInstallationTestEnvironment.UninstallKey, out _));
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellUninstall_RefusesInnoOwned_WithoutDeletingFiles()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation(inno: true);
        using var result = await environment.RunAsync("uninstall", SyntheticInstallationTestEnvironment.OwnedInnoRegistry);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Installation Channel Conflict", result.Output);
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.True(File.Exists(Path.Combine(environment.Target, "unins000.exe")));
        Assert.True(File.Exists(Path.Combine(environment.Target, "unins000.dat")));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("RemoveRegistry") || x.StartsWith("FileSystem/Remove-Item"));
        Assert.True(result.State.GetProperty("Registry").TryGetProperty(SyntheticInstallationTestEnvironment.InnoKey, out _));
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellInstall_RefusesInnoOwned_WithoutReplacingFiles()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation(inno: true);
        using var result = await environment.RunAsync("install", SyntheticInstallationTestEnvironment.OwnedInnoRegistry);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Installation Channel Conflict", result.Output);
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.True(File.Exists(Path.Combine(environment.Target, "unins000.exe")));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("SetRegistry") || x.StartsWith("FileSystem/Move-Item"));
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellInstall_SucceedsForFreshInstall()
    {
        string repoRoot = FindRepositoryRoot();
        string propsContent = File.ReadAllText(Path.Combine(repoRoot, "dotnet", "Directory.Build.props"));
        string canonicalVersion = Regex.Match(propsContent, @"<Version>(?<ver>[^<]+)</Version>").Groups["ver"].Value.Trim();

        using var environment = new SyntheticInstallationTestEnvironment(repoRoot);
        using var result = await environment.RunAsync("install");
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("fresh-payload", File.ReadAllText(environment.Exe));
        var registration = result.State.GetProperty("Registry").GetProperty(SyntheticInstallationTestEnvironment.UninstallKey);
        Assert.Equal("AG2 Router", registration.GetProperty("DisplayName").GetString());
        Assert.Equal("AG2", registration.GetProperty("Publisher").GetString());
        Assert.Equal(environment.Target, registration.GetProperty("InstallLocation").GetString());
        Assert.Equal(canonicalVersion, registration.GetProperty("DisplayVersion").GetString());
        Assert.Equal(1, registration.GetProperty("NoModify").GetInt32());
        Assert.Equal(1, registration.GetProperty("NoRepair").GetInt32());
        Assert.True(File.Exists(environment.StartMenuShortcut));
        Assert.Equal(environment.Exe, result.State.GetProperty("Shortcuts").GetProperty(environment.StartMenuShortcut).GetString());
        Assert.Contains("KnownFolder/Programs:", result.Operations);
        Assert.Contains("ProcessState:", result.Operations);
        Assert.Contains("MutexState:", result.Operations);
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellUninstall_OnAbsentDirectory_CompletesCleanly()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        using var result = await environment.RunAsync("uninstall");
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("No AG2 Router installation detected", result.Output);
        Assert.Empty(result.State.GetProperty("Registry").EnumerateObject());
        Assert.Empty(result.State.GetProperty("Shortcuts").EnumerateObject());
        AssertSyntheticBoundary(environment, result);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    public async Task InstallationOwnership_AmbiguousRegistrations_FailsClosedWithoutMutation(string kind)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        using var result = await environment.RunAsync(kind,
            SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry + "\n" + SyntheticInstallationTestEnvironment.OwnedInnoRegistry);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Ambiguous", result.Output);
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.Equal(2, result.State.GetProperty("Registry").EnumerateObject().Count());
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("SetRegistry") || x.StartsWith("RemoveRegistry") || x.StartsWith("FileSystem/Move-Item"));
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task InstallationOwnership_RegistryIdentity_IgnoresForeignRegistrations()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        const string foreignKey = @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}_is1";
        using var result = await environment.RunAsync("install",
            SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry + "\n" + $$"""
            $global:AG2Synthetic.Registry['{{foreignKey}}'] = @{
                DisplayName = 'Some Other Tool'; Publisher = 'Other'; InstallLocation = $target
            }
            """);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("fresh-payload", File.ReadAllText(environment.Exe));
        Assert.Equal("Some Other Tool", result.State.GetProperty("Registry").GetProperty(foreignKey).GetProperty("DisplayName").GetString());
        Assert.DoesNotContain(result.Operations, x => x.EndsWith(foreignKey));
        AssertSyntheticBoundary(environment, result);
    }

    /// <summary>
    /// Validates whether a PowerShell uninstall registration matches the expected AG2 Router identity.
    /// Implements the exact logic of Test-AG2RouterPowerShellRegistrationMatch in scripts/install.ps1 and scripts/uninstall.ps1.
    /// </summary>
    public static bool IsPowerShellUninstallRegistrationOwned(
        string? displayName,
        string? publisher,
        string? installLocation,
        string? uninstallString,
        string expectedTargetDir)
    {
        if (string.IsNullOrWhiteSpace(displayName) || !string.Equals(displayName.Trim(), "AG2 Router", StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(publisher) || !string.Equals(publisher.Trim(), "AG2", StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(installLocation))
            return false;

        try
        {
            string normInstall = Path.GetFullPath(installLocation).TrimEnd('\\');
            string normTarget = Path.GetFullPath(expectedTargetDir).TrimEnd('\\');
            if (!string.Equals(normInstall, normTarget, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uninstallString))
            return false;

        string lowerUninst = uninstallString.ToLowerInvariant();
        if (!lowerUninst.Contains("uninstall.ps1", StringComparison.Ordinal) && !lowerUninst.Contains("ag2router", StringComparison.Ordinal))
            return false;

        return true;
    }

    [Theory]
    // Valid ownership match
    [InlineData("AG2 Router", "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router", @"powershell.exe -File ""C:\Users\test\AppData\Local\Programs\AG2Router\uninstall.ps1""", @"C:\Users\test\AppData\Local\Programs\AG2Router", true)]
    // Foreign DisplayName
    [InlineData("Other Product", "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router", @"powershell.exe -File ""C:\Users\test\AppData\Local\Programs\AG2Router\uninstall.ps1""", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    // Foreign Publisher
    [InlineData("AG2 Router", "Other Corp", @"C:\Users\test\AppData\Local\Programs\AG2Router", @"powershell.exe -File ""C:\Users\test\AppData\Local\Programs\AG2Router\uninstall.ps1""", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    // Foreign InstallLocation
    [InlineData("AG2 Router", "AG2", @"C:\Program Files\OtherApp", @"powershell.exe -File ""C:\Users\test\AppData\Local\Programs\AG2Router\uninstall.ps1""", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    // Foreign/Invalid UninstallString
    [InlineData("AG2 Router", "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router", @"notepad.exe", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    // Null/empty properties
    [InlineData(null, "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router", @"powershell.exe -File uninstall.ps1", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    [InlineData("AG2 Router", null, @"C:\Users\test\AppData\Local\Programs\AG2Router", @"powershell.exe -File uninstall.ps1", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    [InlineData("AG2 Router", "AG2", null, @"powershell.exe -File uninstall.ps1", @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    [InlineData("AG2 Router", "AG2", @"C:\Users\test\AppData\Local\Programs\AG2Router", null, @"C:\Users\test\AppData\Local\Programs\AG2Router", false)]
    public void PowerShellUninstallRegistration_StrictGuards_RecognizeOnlyOwnedRegistrations(
        string? displayName,
        string? publisher,
        string? installLocation,
        string? uninstallString,
        string expectedTargetDir,
        bool expectedResult)
    {
        bool isOwned = IsPowerShellUninstallRegistrationOwned(displayName, publisher, installLocation, uninstallString, expectedTargetDir);
        Assert.Equal(expectedResult, isOwned);
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellInstall_RefusesExactKeyForeignIdentity_PreservesRegistryAndFiles()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        Directory.CreateDirectory(environment.UserData);
        string userDataFile = Path.Combine(environment.UserData, "accounts.json");
        File.WriteAllText(userDataFile, "synthetic-user-data");

        string foreignSetup = $$"""
            $global:AG2Synthetic.Registry['{{SyntheticInstallationTestEnvironment.UninstallKey}}'] = @{
                DisplayName = 'Foreign Application'
                Publisher = 'ForeignCorp'
                InstallLocation = 'C:\ForeignApp'
                UninstallString = 'C:\ForeignApp\uninstall.exe'
            }
            """;

        using var result = await environment.RunAsync("install", foreignSetup);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Installation Channel Conflict", result.Output);
        Assert.Contains("could not be proven to belong to this installation", result.Output);

        // Registry value-for-value preserved
        var reg = result.State.GetProperty("Registry").GetProperty(SyntheticInstallationTestEnvironment.UninstallKey);
        Assert.Equal("Foreign Application", reg.GetProperty("DisplayName").GetString());
        Assert.Equal("ForeignCorp", reg.GetProperty("Publisher").GetString());
        Assert.Equal(@"C:\ForeignApp", reg.GetProperty("InstallLocation").GetString());
        Assert.Equal(@"C:\ForeignApp\uninstall.exe", reg.GetProperty("UninstallString").GetString());

        // Files, shortcuts, and user data untouched
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.Equal("synthetic-user-data", File.ReadAllText(userDataFile));
        Assert.False(File.Exists(environment.StartMenuShortcut));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("SetRegistry") || x.StartsWith("FileSystem/Move-Item") || x.StartsWith("FileSystem/Copy-Item"));
        AssertSyntheticBoundary(environment, result);
    }

    [Theory]
    // Correct name & publisher, foreign path
    [InlineData("AG2 Router", "AG2", @"C:\OtherPath\AG2Router")]
    // Correct path & publisher, foreign name
    [InlineData("Other Product", "AG2", null)]
    // Correct path & name, foreign publisher
    [InlineData("AG2 Router", "Other Publisher", null)]
    public async Task InstallationOwnership_PowerShellInstall_RefusesMismatchingIdentityAtFixedKey(
        string displayName, string publisher, string? customInstallLocation)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();

        string locExpr = customInstallLocation != null ? $"'{customInstallLocation}'" : "$target";
        string setupScript = $$"""
            $loc = {{locExpr}}
            $global:AG2Synthetic.Registry['{{SyntheticInstallationTestEnvironment.UninstallKey}}'] = @{
                DisplayName = '{{displayName}}'
                Publisher = '{{publisher}}'
                InstallLocation = $loc
                UninstallString = "powershell.exe -File `"$loc\uninstall.ps1`""
            }
            """;

        using var result = await environment.RunAsync("install", setupScript);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Installation Channel Conflict", result.Output);
        Assert.Contains("could not be proven to belong to this installation", result.Output);
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("SetRegistry") || x.StartsWith("FileSystem/Move-Item"));
        AssertSyntheticBoundary(environment, result);
    }

    [Theory]
    [InlineData("@{ Publisher = 'AG2'; InstallLocation = $target; UninstallString = 'powershell.exe -File \"$target\\uninstall.ps1\"' }")]
    [InlineData("@{ DisplayName = 'AG2 Router'; InstallLocation = $target; UninstallString = 'powershell.exe -File \"$target\\uninstall.ps1\"' }")]
    [InlineData("@{ DisplayName = 'AG2 Router'; Publisher = 'AG2'; UninstallString = 'powershell.exe -File \"$target\\uninstall.ps1\"' }")]
    [InlineData("@{ DisplayName = 'AG2 Router'; Publisher = 'AG2'; InstallLocation = $target }")]
    [InlineData("@{ DisplayName = 'AG2 Router'; Publisher = 'AG2'; InstallLocation = $target; UninstallString = 'notepad.exe' }")]
    [InlineData("@{}")]
    public async Task InstallationOwnership_PowerShellInstall_RefusesMalformedOrIncompleteFixedKey(string keyProperties)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();

        string setupScript = $$"""
            $global:AG2Synthetic.Registry['{{SyntheticInstallationTestEnvironment.UninstallKey}}'] = {{keyProperties}}
            """;

        using var result = await environment.RunAsync("install", setupScript);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Installation Channel Conflict", result.Output);
        Assert.Contains("could not be proven to belong to this installation", result.Output);
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("SetRegistry") || x.StartsWith("FileSystem/Move-Item"));
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellInstall_SucceedsForLegitimateUpgrade()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();

        using var result = await environment.RunAsync("install", SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("fresh-payload", File.ReadAllText(environment.Exe));
        var reg = result.State.GetProperty("Registry").GetProperty(SyntheticInstallationTestEnvironment.UninstallKey);
        Assert.Equal("AG2 Router", reg.GetProperty("DisplayName").GetString());
        Assert.Equal("AG2", reg.GetProperty("Publisher").GetString());
        Assert.Equal(environment.Target, reg.GetProperty("InstallLocation").GetString());
        Assert.True(File.Exists(environment.StartMenuShortcut));
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellInstall_RefusesForeignFixedKey_EvenWhenTargetDirectoryEmpty()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        Assert.False(Directory.Exists(environment.Target));

        string foreignSetup = $$"""
            $global:AG2Synthetic.Registry['{{SyntheticInstallationTestEnvironment.UninstallKey}}'] = @{
                DisplayName = 'Foreign Product'
                Publisher = 'Foreign Corp'
                InstallLocation = 'C:\OtherApp'
                UninstallString = 'C:\OtherApp\uninstall.exe'
            }
            """;

        using var result = await environment.RunAsync("install", foreignSetup);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Installation Channel Conflict", result.Output);
        Assert.Contains("could not be proven to belong to this installation", result.Output);

        var reg = result.State.GetProperty("Registry").GetProperty(SyntheticInstallationTestEnvironment.UninstallKey);
        Assert.Equal("Foreign Product", reg.GetProperty("DisplayName").GetString());
        Assert.Equal("Foreign Corp", reg.GetProperty("Publisher").GetString());
        Assert.Equal(@"C:\OtherApp", reg.GetProperty("InstallLocation").GetString());

        Assert.False(File.Exists(environment.Exe));
        Assert.False(File.Exists(environment.StartMenuShortcut));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("SetRegistry") || x.StartsWith("FileSystem/Copy-Item") || x.StartsWith("FileSystem/Move-Item"));
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellInstall_RefusesForeignFixedKey_WithInnoEvidencePresent()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation(inno: true);

        string setupScript = SyntheticInstallationTestEnvironment.OwnedInnoRegistry + "\n" + $$"""
            $global:AG2Synthetic.Registry['{{SyntheticInstallationTestEnvironment.UninstallKey}}'] = @{
                DisplayName = 'Foreign Product'
                Publisher = 'Foreign Corp'
                InstallLocation = 'C:\OtherApp'
                UninstallString = 'C:\OtherApp\uninstall.exe'
            }
            """;

        using var result = await environment.RunAsync("install", setupScript);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Installation Channel Conflict", result.Output);
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.True(File.Exists(Path.Combine(environment.Target, "unins000.exe")));
        Assert.True(result.State.GetProperty("Registry").TryGetProperty(SyntheticInstallationTestEnvironment.InnoKey, out _));
        var reg = result.State.GetProperty("Registry").GetProperty(SyntheticInstallationTestEnvironment.UninstallKey);
        Assert.Equal("Foreign Product", reg.GetProperty("DisplayName").GetString());
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("SetRegistry") || x.StartsWith("FileSystem/Move-Item"));
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellUninstall_RefusesForeignFixedKey_PreservesRegistryAndFiles()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        Directory.CreateDirectory(environment.UserData);
        string userDataFile = Path.Combine(environment.UserData, "accounts.json");
        File.WriteAllText(userDataFile, "synthetic-user-data");

        string foreignSetup = $$"""
            $global:AG2Synthetic.Registry['{{SyntheticInstallationTestEnvironment.UninstallKey}}'] = @{
                DisplayName = 'Foreign Product'
                Publisher = 'Foreign Corp'
                InstallLocation = 'C:\OtherApp'
                UninstallString = 'C:\OtherApp\uninstall.exe'
            }
            """;

        using var result = await environment.RunAsync("uninstall", foreignSetup);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Installation Channel Conflict", result.Output);
        Assert.Contains("could not be proven to belong to this installation", result.Output);

        var reg = result.State.GetProperty("Registry").GetProperty(SyntheticInstallationTestEnvironment.UninstallKey);
        Assert.Equal("Foreign Product", reg.GetProperty("DisplayName").GetString());
        Assert.Equal("Foreign Corp", reg.GetProperty("Publisher").GetString());
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.Equal("synthetic-user-data", File.ReadAllText(userDataFile));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("RemoveRegistry") || x.StartsWith("FileSystem/Remove-Item"));
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task SyntheticInstallation_Uninstall_RemovesOnlySyntheticShortcutsAndRegistry_PreservesData()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        Directory.CreateDirectory(environment.UserData);
        string data = Path.Combine(environment.UserData, "accounts.json");
        File.WriteAllText(data, "synthetic-user-data");
        using var result = await environment.RunAsync("uninstall", $$"""
            Invoke-SyntheticInstallation -Kind install
            $exe = Join-Path $target 'AG2Router.exe'
            $desktop = Join-Path $global:AG2Synthetic.Root 'Desktop\AG2 Router.lnk'
            & $global:AG2InstallationEnvironment.CreateShortcut @{ Path = $desktop; Target = $exe; WorkingDirectory = $target }
            $global:AG2Synthetic.Registry['{{SyntheticInstallationTestEnvironment.RunKey}}'] = @{ AG2Router = '"' + $exe + '" --tray'; Other = 'synthetic-other' }
            """);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(Directory.Exists(environment.Target));
        Assert.False(File.Exists(environment.StartMenuShortcut));
        Assert.False(File.Exists(environment.DesktopShortcut));
        Assert.Empty(result.State.GetProperty("Shortcuts").EnumerateObject());
        Assert.False(result.State.GetProperty("Registry").TryGetProperty(SyntheticInstallationTestEnvironment.UninstallKey, out _));
        var run = result.State.GetProperty("Registry").GetProperty(SyntheticInstallationTestEnvironment.RunKey);
        Assert.False(run.TryGetProperty("AG2Router", out _));
        Assert.Equal("synthetic-other", run.GetProperty("Other").GetString());
        Assert.Equal("synthetic-user-data", File.ReadAllText(data));
        Assert.Contains("ShortcutTarget:" + environment.StartMenuShortcut, result.Operations);
        Assert.Contains("ShortcutTarget:" + environment.DesktopShortcut, result.Operations);
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task SyntheticInstallation_Uninstall_PreservesForeignShortcutAndStartupValue()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        using var result = await environment.RunAsync("uninstall",
            SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry + "\n" + $$"""
            $foreign = Join-Path $global:AG2Synthetic.Root 'SourcePayload\Other.exe'
            $shortcut = Join-Path $global:AG2Synthetic.Root 'StartMenu\AG2 Router.lnk'
            & $global:AG2InstallationEnvironment.CreateShortcut @{ Path = $shortcut; Target = $foreign; WorkingDirectory = $target }
            $global:AG2Synthetic.Registry['{{SyntheticInstallationTestEnvironment.RunKey}}'] = @{ AG2Router = '"' + $foreign + '" --tray' }
            """);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(File.Exists(environment.StartMenuShortcut));
        Assert.True(result.State.GetProperty("Shortcuts").TryGetProperty(environment.StartMenuShortcut, out _));
        Assert.True(result.State.GetProperty("Registry").GetProperty(SyntheticInstallationTestEnvironment.RunKey).TryGetProperty("AG2Router", out _));
        AssertSyntheticBoundary(environment, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticInstallation_MissingRegistryKeys_AreAbsenceIncludingLegacyOverride(bool legacyOverride)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        using var result = await environment.RunAsync("install",
            extraParameters: legacyOverride ? "@{ RegistryOverride = @{} }" : "@{}");
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Single(result.State.GetProperty("Registry").EnumerateObject());
        Assert.Contains(result.Operations, x => x.StartsWith("RegistryExists:HKLM:"));
        if (legacyOverride) Assert.DoesNotContain(result.Operations, x => x.StartsWith("ReadRegistry:"));
        else Assert.Contains("ReadRegistry:" + SyntheticInstallationTestEnvironment.InnoKey, result.Operations);
        AssertSyntheticBoundary(environment, result);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    public async Task SyntheticInstallation_RunningProcessAndMutex_ShutdownIsSimulated(string kind)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        using var result = await environment.RunAsync(kind,
            SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry + "\n" + """
            $global:AG2Synthetic.ProcessState = 'RUNNING'
            $global:AG2Synthetic.MutexState = 'RUNNING'
            """);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(1, result.State.GetProperty("ExitRequests").GetInt32());
        Assert.Equal("STOPPED", result.State.GetProperty("ProcessState").GetString());
        Assert.Equal("STOPPED", result.State.GetProperty("MutexState").GetString());
        Assert.Contains("StartExitProcess:" + environment.Exe, result.Operations);
        AssertSyntheticBoundary(environment, result);
    }

    [Theory]
    [InlineData("UNKNOWN", "STOPPED")]
    [InlineData("STOPPED", "UNKNOWN")]
    [InlineData("UNKNOWN", "UNKNOWN")]
    public async Task SyntheticInstallation_Install_UnresolvedUnknownState_FailsClosedAndPreservesExistingInstallation(string processState, string mutexState)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        using var result = await environment.RunAsync("install",
            SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry + "\n" + $$"""
            $global:AG2Synthetic.ProcessState = '{{processState}}'
            $global:AG2Synthetic.MutexState = '{{mutexState}}'
            $global:AG2Synthetic.Registry['{{SyntheticInstallationTestEnvironment.RunKey}}'] = @{ AG2Router = '"' + $target + '\AG2Router.exe" --tray' }
            """);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("stopped state could not be verified", result.Output);
        Assert.Contains("Refusing to replace application files while instance state is unknown", result.Output);
        Assert.Equal(0, result.State.GetProperty("ExitRequests").GetInt32());
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.True(result.State.GetProperty("Registry").GetProperty(SyntheticInstallationTestEnvironment.RunKey).TryGetProperty("AG2Router", out _));
        Assert.False(File.Exists(environment.StartMenuShortcut));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("FileSystem/Move-Item") || x.StartsWith("FileSystem/Copy-Item") || x.StartsWith("SetRegistry"));
        AssertSyntheticBoundary(environment, result);
    }

    [Theory]
    [InlineData("RUNNING", "STOPPED")]
    [InlineData("STOPPED", "RUNNING")]
    public async Task SyntheticInstallation_Install_SingleRunningComponent_ShutsDownGracefullyAndProceeds(string processState, string mutexState)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        using var result = await environment.RunAsync("install",
            SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry + "\n" + $$"""
            $global:AG2Synthetic.ProcessState = '{{processState}}'
            $global:AG2Synthetic.MutexState = '{{mutexState}}'
            """);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(1, result.State.GetProperty("ExitRequests").GetInt32());
        Assert.Equal("STOPPED", result.State.GetProperty("ProcessState").GetString());
        Assert.Equal("STOPPED", result.State.GetProperty("MutexState").GetString());
        Assert.Equal("fresh-payload", File.ReadAllText(environment.Exe));
        Assert.Contains("StartExitProcess:" + environment.Exe, result.Operations);
        AssertSyntheticBoundary(environment, result);
    }

    [Theory]
    [InlineData("RUNNING", "UNKNOWN")]
    [InlineData("UNKNOWN", "RUNNING")]
    public async Task SyntheticInstallation_Install_RunningWithUnknown_CallsExitAndFailsClosedIfUnknownRemains(string processState, string mutexState)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        string setupScript = processState == "RUNNING"
            ? $$"""
                $global:AG2Synthetic.ProcessState = '{{processState}}'
                1..30 | ForEach-Object { $global:AG2Synthetic.MutexStates.Enqueue('{{mutexState}}') }
                """
            : $$"""
                $global:AG2Synthetic.MutexState = '{{mutexState}}'
                1..30 | ForEach-Object { $global:AG2Synthetic.ProcessStates.Enqueue('{{processState}}') }
                """;

        using var result = await environment.RunAsync("install",
            SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry + "\n" + setupScript);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(1, result.State.GetProperty("ExitRequests").GetInt32());
        Assert.Contains("could not be verified after shutdown request", result.Output);
        Assert.Contains("Refusing to replace application files while instance state is unknown", result.Output);
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("FileSystem/Move-Item") || x.StartsWith("SetRegistry"));
        AssertSyntheticBoundary(environment, result);
    }

    [Fact]
    public async Task SyntheticInstallation_Install_QueryFailureAfterShutdownAttempt_AbortsBeforeMutation()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        using var result = await environment.RunAsync("install",
            SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry + "\n" + """
            $global:AG2Synthetic.ProcessStates.Enqueue('RUNNING')
            1..30 | ForEach-Object { $global:AG2Synthetic.ProcessStates.Enqueue('UNKNOWN') }
            $global:AG2Synthetic.MutexState = 'STOPPED'
            """);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(1, result.State.GetProperty("ExitRequests").GetInt32());
        Assert.Contains("could not be verified after shutdown request", result.Output);
        Assert.Contains("Refusing to replace application files while instance state is unknown", result.Output);
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("FileSystem/Move-Item") || x.StartsWith("SetRegistry"));
        AssertSyntheticBoundary(environment, result);
    }

    [Theory]
    [InlineData("Process")]
    [InlineData("Mutex")]
    public async Task SyntheticInstallation_Install_TransientUnknownResolvesToStopped_ProceedsAfterProof(string transientComponent)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        string queueSetup = transientComponent == "Process"
            ? """
                $global:AG2Synthetic.ProcessStates.Enqueue('UNKNOWN')
                $global:AG2Synthetic.ProcessStates.Enqueue('STOPPED')
                $global:AG2Synthetic.MutexState = 'STOPPED'
                """
            : """
                $global:AG2Synthetic.MutexStates.Enqueue('UNKNOWN')
                $global:AG2Synthetic.MutexStates.Enqueue('STOPPED')
                $global:AG2Synthetic.ProcessState = 'STOPPED'
                """;

        using var result = await environment.RunAsync("install",
            SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry + "\n" + queueSetup);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(0, result.State.GetProperty("ExitRequests").GetInt32());
        Assert.Equal("fresh-payload", File.ReadAllText(environment.Exe));
        AssertSyntheticBoundary(environment, result);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    public async Task SyntheticInstallation_HungShutdown_AbortsBeforeMutation(string kind)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.SeedInstallation();
        using var result = await environment.RunAsync(kind,
            SyntheticInstallationTestEnvironment.OwnedPowerShellRegistry + "\n" + """
            $global:AG2Synthetic.ProcessState = 'RUNNING'
            $global:AG2Synthetic.ShutdownCompletes = $false
            """);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("did not finish", result.Output);
        Assert.Equal(1, result.State.GetProperty("ExitRequests").GetInt32());
        Assert.Equal("existing-payload", File.ReadAllText(environment.Exe));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("RemoveRegistry") || x.StartsWith("FileSystem/Move-Item"));
        AssertSyntheticBoundary(environment, result);
    }

    [Theory]
    [InlineData("ReadRegistry")]
    [InlineData("KnownFolder")]
    [InlineData("ProcessState")]
    [InlineData("MutexState")]
    [InlineData("CreateShortcut")]
    [InlineData("SetRegistry")]
    [InlineData("FileSystem")]
    public async Task SyntheticInstallation_MissingDependency_FailsEvenWhenScriptCatchesError(string operation)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        using var result = await environment.RunAsync("install",
            "$global:AG2InstallationEnvironment.Remove('" + operation + "')");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.BoundaryErrors, x => x.Contains("'" + operation + "'"));
    }

    [Fact]
    public async Task SyntheticInstallation_OutsideKnownFolder_IsRejectedBeforeShortcutAccess()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        using var result = await environment.RunAsync("install", """
            $global:AG2InstallationEnvironment.KnownFolder = { param($p) return $global:AG2Synthetic.Root + '-outside' }
            """);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.BoundaryErrors, x => x.Contains("outside synthetic root"));
        Assert.False(File.Exists(environment.StartMenuShortcut));
        Assert.Empty(result.State.GetProperty("Shortcuts").EnumerateObject());
        Assert.All(result.FilePaths, path => AssertUnderRoot(environment.Root, path));
    }

    [Fact]
    public async Task SyntheticInstallation_OutsideSourcePath_IsRejectedBeforeFilesystemAccess()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        using var result = await environment.RunAsync("install", extraParameters: "@{ SourceDir = $global:AG2Synthetic.Root + '-outside' }");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.BoundaryErrors, x => x.Contains("outside synthetic root"));
        Assert.False(File.Exists(environment.Exe));
        Assert.All(result.FilePaths, path => AssertUnderRoot(environment.Root, path));
    }

    [Theory]
    [InlineData("HKCU:\\Software\\AG2TestSentinel")]
    [InlineData("Registry::HKEY_CURRENT_USER\\Software\\AG2TestSentinel")]
    [InlineData("C:relative-path")]
    public async Task SyntheticInstallation_NonFilesystemPath_IsRejectedBeforeProviderAccess(string path)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.AppendScript("install", "\nTest-AG2RouterFile -LiteralPath '" + path + "'\n");
        using var result = await environment.RunAsync("install");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.BoundaryErrors, x => x.Contains("Invalid synthetic filesystem path"));
        Assert.All(result.FilePaths, file => AssertUnderRoot(environment.Root, file));
    }

    [Theory]
    [InlineData("Get-ItemProperty -LiteralPath 'HKCU:\\Software\\AG2TestSentinel'")]
    [InlineData("Set-ItemProperty -LiteralPath 'HKCU:\\Software\\AG2TestSentinel' -Name Test -Value 1")]
    [InlineData("[System.Environment]::GetFolderPath('Programs')")]
    [InlineData("Get-Process -Name AG2Router")]
    [InlineData("[System.Threading.Mutex]::OpenExisting('Local\\AG2Router_Session_Mutex')")]
    [InlineData("Start-Process AG2Router.exe")]
    [InlineData("Remove-Item 'unmocked-file'")]
    [InlineData("& ('Get-' + 'Process')")]
    public async Task SyntheticInstallation_UnmockedHostOperation_IsRejectedBeforeExecution(string operation)
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.AppendScript("install", "\n" + operation + "\n");
        using var result = await environment.RunAsync("install");
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.BoundaryErrors);
        Assert.Empty(result.Operations);
        Assert.False(Directory.Exists(environment.Target));
    }

    [Fact]
    public async Task SyntheticInstallation_ProductionAdapterFallback_IsUnavailable()
    {
        using var environment = new SyntheticInstallationTestEnvironment(FindRepositoryRoot());
        environment.AppendScript("install", "\nInvoke-AG2RouterProductionEnvironment -Operation ProcessState\n");
        using var result = await environment.RunAsync("install");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.BoundaryErrors, x => x.Contains("Production adapter reached"));
        Assert.DoesNotContain(result.Operations, x => x.StartsWith("StartExitProcess"));
    }

    private static void AssertSyntheticBoundary(SyntheticInstallationTestEnvironment environment, SyntheticInstallationTestEnvironment.Result result)
    {
        Assert.Empty(result.BoundaryErrors);
        Assert.NotEmpty(result.FilePaths);
        Assert.All(result.FilePaths, path => AssertUnderRoot(environment.Root, path));
    }

    private static void AssertUnderRoot(string root, string path) =>
        Assert.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    #endregion

    private static string FindRepositoryRoot()

    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static bool IsCanonicalReleaseTag(string canonicalVersion, string tagName) =>
        string.Equals(tagName, $"v{canonicalVersion}", StringComparison.Ordinal);

    #endregion
}
