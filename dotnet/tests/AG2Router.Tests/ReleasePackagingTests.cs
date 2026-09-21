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
}
