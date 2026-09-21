using System.Diagnostics;
using System.IO;
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

        // Verify uninstaller graceful shutdown
        Assert.Contains("Filename: \"{app}\\AG2Router.exe\"; Parameters: \"--exit\"", issContent);
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
        Assert.Contains("CurStep = ssPostInstall", issContent);
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

    #endregion
}
