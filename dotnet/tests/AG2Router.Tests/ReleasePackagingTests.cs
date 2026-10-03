using System.Diagnostics;
using System.IO;
using System.Text;
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
        Assert.Equal("0.4.0", canonicalVersion);

        // Assembly, File, and Informational versions must match canonical version
        Assert.Contains($"<AssemblyVersion>{canonicalVersion}</AssemblyVersion>", propsContent);
        Assert.Contains($"<FileVersion>{canonicalVersion}</FileVersion>", propsContent);
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
        string script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "uninstall.ps1"));
        string stopFunctions = script.Split("# 2. Graceful Process Shutdown via Named Mutex and Process Check")[1]
            .Split("Assert-AG2RouterStopped -InstalledExe $ExePath")[0];
        string scenario = failAfterExit ? "post" : "pre";
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $script:destructiveCalls = 0
            function Remove-Item { $script:destructiveCalls++ }
            {{stopFunctions}}
            $script:queries = 0
            $script:exitCalls = 0
            function Get-Process {
                param([string]$ErrorAction)
                $script:queries++
                [Console]::Error.WriteLine('phase: process query ' + $script:queries)
                if ('{{scenario}}' -eq 'post' -and $script:queries -eq 1) {
                    return [pscustomobject]@{ ProcessName = 'AG2Router' }
                }
                throw 'Synthetic process query failure'
            }
            function Get-AG2RouterMutexState { return 'STOPPED' }
            function Invoke-AG2RouterExitRequest {
                param([string]$InstalledExe)
                $script:exitCalls++
                [Console]::Error.WriteLine('phase: exit request')
            }
            $caught = $false
            try {
                Assert-AG2RouterStopped -InstalledExe 'X:\synthetic\AG2Router.exe'
                Remove-Item 'X:\synthetic\programs'
            } catch {
                $caught = $true
                if ($_.Exception.Message -notmatch 'process state.*(could not be verified|became unknown)') { exit 2 }
            }
            if (-not $caught -or $script:destructiveCalls -ne 0) { exit 3 }
            if ('{{scenario}}' -eq 'post') {
                if ($script:queries -ne 2 -or $script:exitCalls -ne 1) { exit 4 }
            } elseif ($script:queries -ne 1 -or $script:exitCalls -ne 0) { exit 5 }
            exit 0
            """;

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("PowerShell test process did not start.");
        Task<string> standardOutput = child.StandardOutput.ReadToEndAsync();
        Task<string> standardError = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            if (!child.HasExited)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            throw new TimeoutException(
                $"Synthetic uninstall safety check did not complete. stdout: {await standardOutput} stderr: {await standardError}");
        }
        string output = await standardOutput;
        string error = await standardError;
        Assert.True(child.ExitCode == 0,
            $"Synthetic {scenario} query failure exited {child.ExitCode}: {output} {error}");
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
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunPowerShellCommandAsync(string scriptText, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(scriptText)));

        using var child = Process.Start(start) ?? throw new InvalidOperationException("PowerShell test process failed to start.");
        var outTask = child.StandardOutput.ReadToEndAsync();
        var errTask = child.StandardError.ReadToEndAsync();

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        try
        {
            await child.WaitForExitAsync().WaitAsync(effectiveTimeout);
        }
        catch (TimeoutException)
        {
            try { child.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"PowerShell script timed out after {effectiveTimeout}. stdout: {await outTask} stderr: {await errTask}");
        }

        return (child.ExitCode, await outTask, await errTask);
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellUninstall_SucceedsForPowerShellOwned()
    {
        string repoRoot = FindRepositoryRoot();
        string uninstallScriptPath = Path.Combine(repoRoot, "scripts", "uninstall.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"AG2_Test_PsUninst_{Guid.NewGuid():N}");
        string localApp = Path.Combine(tempDir, "LocalApp");
        string targetDir = Path.Combine(localApp, "Programs", "AG2Router");

        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "AG2Router.exe"), "dummy-exe");
        File.Copy(uninstallScriptPath, Path.Combine(targetDir, "uninstall.ps1"));

        try
        {
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                $env:LOCALAPPDATA = '{{localApp.Replace("'", "''")}}'
                $target = '{{targetDir.Replace("'", "''")}}'
                $regOverride = @{
                    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AG2Router' = @{
                        DisplayName = 'AG2 Router'
                        Publisher = 'AG2'
                        InstallLocation = $target
                        UninstallString = "powershell.exe -File `"$target\uninstall.ps1`""
                    }
                }
                function Get-AG2RouterProcessState { return 'STOPPED' }
                function Get-AG2RouterMutexState { return 'STOPPED' }

                & '{{uninstallScriptPath.Replace("'", "''")}}' -RegistryOverride $regOverride
                """;

            var (exitCode, stdout, stderr) = await RunPowerShellCommandAsync(command);
            Assert.True(exitCode == 0, $"PowerShell uninstall failed ({exitCode}): stdout: {stdout} stderr: {stderr}");
            Assert.False(Directory.Exists(targetDir), "Target program directory must be removed for PowerShellOwned installation.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellUninstall_RefusesInnoOwned_WithoutDeletingFiles()
    {
        string repoRoot = FindRepositoryRoot();
        string uninstallScriptPath = Path.Combine(repoRoot, "scripts", "uninstall.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"AG2_Test_InnoUninst_{Guid.NewGuid():N}");
        string localApp = Path.Combine(tempDir, "LocalApp");
        string targetDir = Path.Combine(localApp, "Programs", "AG2Router");

        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "AG2Router.exe"), "inno-app");
        File.WriteAllText(Path.Combine(targetDir, "unins000.exe"), "inno-uninstaller");
        File.WriteAllText(Path.Combine(targetDir, "unins000.dat"), "inno-data");
        File.Copy(uninstallScriptPath, Path.Combine(targetDir, "uninstall.ps1"));

        try
        {
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                $env:LOCALAPPDATA = '{{localApp.Replace("'", "''")}}'
                $target = '{{targetDir.Replace("'", "''")}}'
                $innoKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{{CanonicalInnoKeyName}}"
                $regOverride = @{
                    $innoKey = @{
                        DisplayName = 'AG2 Router'
                        Publisher = 'AG2'
                        InstallLocation = $target
                        UninstallString = "`"$target\unins000.exe`""
                    }
                }
                function Get-AG2RouterProcessState { return 'STOPPED' }
                function Get-AG2RouterMutexState { return 'STOPPED' }

                & '{{uninstallScriptPath.Replace("'", "''")}}' -RegistryOverride $regOverride
                """;

            var (exitCode, stdout, stderr) = await RunPowerShellCommandAsync(command);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("Installation Channel Conflict", stderr + stdout);
            Assert.True(Directory.Exists(targetDir), "Target program directory must NOT be deleted for InnoOwned installation.");
            Assert.True(File.Exists(Path.Combine(targetDir, "unins000.exe")), "Inno uninstaller must NOT be deleted.");
            Assert.True(File.Exists(Path.Combine(targetDir, "unins000.dat")), "Inno data must NOT be deleted.");
            Assert.True(File.Exists(Path.Combine(targetDir, "AG2Router.exe")), "Application binary must NOT be deleted.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellInstall_RefusesInnoOwned_WithoutReplacingFiles()
    {
        string repoRoot = FindRepositoryRoot();
        string installScriptPath = Path.Combine(repoRoot, "scripts", "install.ps1");
        string uninstallScriptPath = Path.Combine(repoRoot, "scripts", "uninstall.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"AG2_Test_InnoInstall_{Guid.NewGuid():N}");
        string localApp = Path.Combine(tempDir, "LocalApp");
        string targetDir = Path.Combine(localApp, "Programs", "AG2Router");
        string sourceDir = Path.Combine(tempDir, "SourcePayload");

        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "AG2Router.exe"), "new-powershell-payload");
        File.Copy(uninstallScriptPath, Path.Combine(sourceDir, "uninstall.ps1"));

        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "AG2Router.exe"), "existing-inno-payload");
        File.WriteAllText(Path.Combine(targetDir, "unins000.exe"), "inno-uninstaller");

        try
        {
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                $env:LOCALAPPDATA = '{{localApp.Replace("'", "''")}}'
                $target = '{{targetDir.Replace("'", "''")}}'
                $innoKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{{CanonicalInnoKeyName}}"
                $regOverride = @{
                    $innoKey = @{
                        DisplayName = 'AG2 Router'
                        Publisher = 'AG2'
                        InstallLocation = $target
                        UninstallString = "`"$target\unins000.exe`""
                    }
                }
                function Get-AG2RouterProcessState { return 'STOPPED' }
                function Get-AG2RouterMutexState { return 'STOPPED' }

                & '{{installScriptPath.Replace("'", "''")}}' -SourceDir '{{sourceDir.Replace("'", "''")}}' -RegistryOverride $regOverride
                """;

            var (exitCode, stdout, stderr) = await RunPowerShellCommandAsync(command);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("Installation Channel Conflict", stderr + stdout);
            Assert.Equal("existing-inno-payload", File.ReadAllText(Path.Combine(targetDir, "AG2Router.exe")));
            Assert.True(File.Exists(Path.Combine(targetDir, "unins000.exe")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellInstall_SucceedsForFreshInstall()
    {
        string repoRoot = FindRepositoryRoot();
        string installScriptPath = Path.Combine(repoRoot, "scripts", "install.ps1");
        string uninstallScriptPath = Path.Combine(repoRoot, "scripts", "uninstall.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"AG2_Test_FreshInstall_{Guid.NewGuid():N}");
        string localApp = Path.Combine(tempDir, "LocalApp");
        string targetDir = Path.Combine(localApp, "Programs", "AG2Router");
        string sourceDir = Path.Combine(tempDir, "SourcePayload");

        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "AG2Router.exe"), "fresh-payload");
        File.Copy(uninstallScriptPath, Path.Combine(sourceDir, "uninstall.ps1"));

        try
        {
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                $env:LOCALAPPDATA = '{{localApp.Replace("'", "''")}}'
                $regOverride = @{}
                function Get-AG2RouterProcessState { return 'STOPPED' }
                function Get-AG2RouterMutexState { return 'STOPPED' }

                & '{{installScriptPath.Replace("'", "''")}}' -SourceDir '{{sourceDir.Replace("'", "''")}}' -RegistryOverride $regOverride
                """;

            var (exitCode, stdout, stderr) = await RunPowerShellCommandAsync(command);
            Assert.True(exitCode == 0, $"Fresh install failed ({exitCode}): stdout: {stdout} stderr: {stderr}");
            Assert.True(File.Exists(Path.Combine(targetDir, "AG2Router.exe")));
            Assert.Equal("fresh-payload", File.ReadAllText(Path.Combine(targetDir, "AG2Router.exe")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task InstallationOwnership_PowerShellUninstall_OnAbsentDirectory_CompletesCleanly()
    {
        string repoRoot = FindRepositoryRoot();
        string uninstallScriptPath = Path.Combine(repoRoot, "scripts", "uninstall.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"AG2_Test_AbsentUninst_{Guid.NewGuid():N}");
        string localApp = Path.Combine(tempDir, "LocalApp");

        try
        {
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                $env:LOCALAPPDATA = '{{localApp.Replace("'", "''")}}'
                $regOverride = @{}

                & '{{uninstallScriptPath.Replace("'", "''")}}' -RegistryOverride $regOverride
                """;

            var (exitCode, stdout, stderr) = await RunPowerShellCommandAsync(command);
            Assert.True(exitCode == 0, $"Uninstall on absent directory failed ({exitCode}): stdout: {stdout} stderr: {stderr}");
            Assert.Contains("No AG2 Router installation detected", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task InstallationOwnership_AmbiguousRegistrations_FailsClosedWithoutMutation()
    {
        string repoRoot = FindRepositoryRoot();
        string installScriptPath = Path.Combine(repoRoot, "scripts", "install.ps1");
        string uninstallScriptPath = Path.Combine(repoRoot, "scripts", "uninstall.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"AG2_Test_Ambiguous_{Guid.NewGuid():N}");
        string localApp = Path.Combine(tempDir, "LocalApp");
        string targetDir = Path.Combine(localApp, "Programs", "AG2Router");
        string sourceDir = Path.Combine(tempDir, "SourcePayload");

        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "AG2Router.exe"), "source-exe");
        File.Copy(uninstallScriptPath, Path.Combine(sourceDir, "uninstall.ps1"));

        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "AG2Router.exe"), "target-exe");
        File.Copy(uninstallScriptPath, Path.Combine(targetDir, "uninstall.ps1"));

        try
        {
            string innoKey = $"HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{CanonicalInnoKeyName}";
            string psKey = "HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\AG2Router";

            // Test 1: install.ps1 fails closed when both registrations exist
            string installCommand = $$"""
                $ErrorActionPreference = 'Stop'
                $env:LOCALAPPDATA = '{{localApp.Replace("'", "''")}}'
                $target = '{{targetDir.Replace("'", "''")}}'
                $regOverride = @{
                    '{{innoKey.Replace("'", "''")}}' = @{
                        DisplayName = 'AG2 Router'
                        Publisher = 'AG2'
                        InstallLocation = $target
                    }
                    '{{psKey.Replace("'", "''")}}' = @{
                        DisplayName = 'AG2 Router'
                        Publisher = 'AG2'
                        InstallLocation = $target
                        UninstallString = "powershell.exe -File `"$target\uninstall.ps1`""
                    }
                }
                function Get-AG2RouterProcessState { return 'STOPPED' }
                function Get-AG2RouterMutexState { return 'STOPPED' }

                & '{{installScriptPath.Replace("'", "''")}}' -SourceDir '{{sourceDir.Replace("'", "''")}}' -RegistryOverride $regOverride
                """;

            var (installExit, installOut, installErr) = await RunPowerShellCommandAsync(installCommand);
            Assert.NotEqual(0, installExit);
            Assert.Contains("Ambiguous", installOut + installErr);
            Assert.Equal("target-exe", File.ReadAllText(Path.Combine(targetDir, "AG2Router.exe")));

            // Test 2: uninstall.ps1 fails closed when both registrations exist
            string uninstallCommand = $$"""
                $ErrorActionPreference = 'Stop'
                $env:LOCALAPPDATA = '{{localApp.Replace("'", "''")}}'
                $target = '{{targetDir.Replace("'", "''")}}'
                $regOverride = @{
                    '{{innoKey.Replace("'", "''")}}' = @{
                        DisplayName = 'AG2 Router'
                        Publisher = 'AG2'
                        InstallLocation = $target
                    }
                    '{{psKey.Replace("'", "''")}}' = @{
                        DisplayName = 'AG2 Router'
                        Publisher = 'AG2'
                        InstallLocation = $target
                        UninstallString = "powershell.exe -File `"$target\uninstall.ps1`""
                    }
                }
                function Get-AG2RouterProcessState { return 'STOPPED' }
                function Get-AG2RouterMutexState { return 'STOPPED' }

                & '{{uninstallScriptPath.Replace("'", "''")}}' -RegistryOverride $regOverride
                """;

            var (uninstExit, uninstOut, uninstErr) = await RunPowerShellCommandAsync(uninstallCommand);
            Assert.NotEqual(0, uninstExit);
            Assert.Contains("Ambiguous", uninstOut + uninstErr);
            Assert.True(Directory.Exists(targetDir));
            Assert.Equal("target-exe", File.ReadAllText(Path.Combine(targetDir, "AG2Router.exe")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task InstallationOwnership_RegistryIdentity_IgnoresForeignRegistrations()
    {
        string repoRoot = FindRepositoryRoot();
        string installScriptPath = Path.Combine(repoRoot, "scripts", "install.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"AG2_Test_ForeignReg_{Guid.NewGuid():N}");
        string localApp = Path.Combine(tempDir, "LocalApp");
        string targetDir = Path.Combine(localApp, "Programs", "AG2Router");

        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "AG2Router.exe"), "installed-exe");

        try
        {
            // Register an unrelated Inno app with a different GUID and test ownership classification
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                $target = '{{targetDir.Replace("'", "''")}}'
                $foreignInnoKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}_is1'
                $regOverride = @{
                    $foreignInnoKey = @{
                        DisplayName = 'Some Other Tool'
                        Publisher = 'Other'
                        InstallLocation = $target
                    }
                    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AG2Router' = @{
                        DisplayName = 'AG2 Router'
                        Publisher = 'AG2'
                        InstallLocation = $target
                        UninstallString = "powershell.exe -File `"$target\uninstall.ps1`""
                    }
                }

                $scriptContent = Get-Content '{{installScriptPath.Replace("'", "''")}}' -Raw
                $funcStart = $scriptContent.IndexOf('function Get-AG2RouterRegistryValue')
                $funcEnd = $scriptContent.IndexOf('Write-Host "===================================================="')
                $funcCode = $scriptContent.Substring($funcStart, $funcEnd - $funcStart)
                Invoke-Expression $funcCode

                $ownership = Get-AG2RouterInstallationOwnership -TargetDir $target -RegistryOverride $regOverride
                Write-Output ("STATUS:" + $ownership.Status)
                """;

            var (exitCode, stdout, stderr) = await RunPowerShellCommandAsync(command);
            Assert.True(exitCode == 0, $"Command failed: {stdout} {stderr}");
            Assert.Contains("STATUS:PowerShellOwned", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

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
