using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AG2Router.Windows.Lifecycle;
using Xunit;

namespace AG2Router.Tests;

public class ReleasePackagingTests
{
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
        var guard = new SingleInstanceGuard();

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
            bool exited = SingleInstanceGuard.RequestExitAndWait(timeoutMs: 3000);
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
        bool exited = SingleInstanceGuard.RequestExitAndWait(timeoutMs: 500);
        sw.Stop();

        Assert.True(exited, "Must report true when primary is already not running");
        Assert.True(sw.ElapsedMilliseconds < 1000, "Must return promptly without hanging");
    }

    [Fact]
    public async Task SingleInstanceGuard_RequestExitAndWait_WhenPrimaryHangs_TimesOutAndReturnsFalse()
    {
        var guard = new SingleInstanceGuard();
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
            bool exited = SingleInstanceGuard.RequestExitAndWait(timeoutMs: 300);
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
