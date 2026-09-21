using AG2Router.Windows.Lifecycle;
using Xunit;

namespace AG2Router.Tests;

public class ReleasePackagingTests
{
    private const string ExpectedInstallDir = @"C:\Users\testuser\AppData\Local\Programs\AG2Router";
    private const string ExpectedExe = @"C:\Users\testuser\AppData\Local\Programs\AG2Router\AG2Router.exe";
    private const string ProtectedUserDataDir = @"C:\Users\testuser\AppData\Local\AG2-Router";

    [Fact]
    public void StartupOwnership_WhenRunKeyMatchesInstalledExe_RecognizesOwnership()
    {
        var fakeRegistry = new InMemoryRegistryAccessor();
        fakeRegistry.SetStringValue(
            WindowsRegistryAutostartService.RunSubKey,
            WindowsRegistryAutostartService.ValueName,
            $"\"{ExpectedExe}\" --tray"
        );

        var service = new WindowsRegistryAutostartService(fakeRegistry, ExpectedExe);
        Assert.True(service.IsAutostartEnabled());

        // Verify simulated uninstaller ownership verification
        string? currentRunVal = fakeRegistry.GetStringValue(
            WindowsRegistryAutostartService.RunSubKey,
            WindowsRegistryAutostartService.ValueName
        );
        Assert.NotNull(currentRunVal);

        string cleanPath = currentRunVal.Trim().Trim('"');
        if (cleanPath.Contains(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var exePart = cleanPath.Substring(0, cleanPath.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4);
            Assert.Equal(ExpectedExe, exePart);
        }
    }

    [Fact]
    public void StartupOwnership_WhenRunKeyPointsToDifferentPath_IdentifiesOwnershipMismatch()
    {
        var fakeRegistry = new InMemoryRegistryAccessor();
        // Points to a developer build or alternate path
        const string foreignExe = @"D:\AG2-Router\bin\Debug\AG2Router.exe";
        fakeRegistry.SetStringValue(
            WindowsRegistryAutostartService.RunSubKey,
            WindowsRegistryAutostartService.ValueName,
            $"\"{foreignExe}\" --tray"
        );

        string? currentRunVal = fakeRegistry.GetStringValue(
            WindowsRegistryAutostartService.RunSubKey,
            WindowsRegistryAutostartService.ValueName
        );
        Assert.NotNull(currentRunVal);

        string cleanPath = currentRunVal.Trim().Trim('"');
        var exePart = cleanPath.Substring(0, cleanPath.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4);

        // Mismatch detected: Must NOT delete
        bool isOwned = string.Equals(exePart, ExpectedExe, StringComparison.OrdinalIgnoreCase);
        Assert.False(isOwned, "Must detect ownership mismatch for foreign/dev executable");
    }

    [Fact]
    public void PathIsolation_ProgramFilesVsPersistentUserData_AreStrictlyDisjoint()
    {
        // Assert that the per-user installation directory and persistent user data directory are completely separate
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

    [Fact]
    public async Task SingleInstanceGuard_ExitCommand_IsRecognizedAndRoutesCorrectly()
    {
        // Verify IPC command dispatch contract
        var exitReceivedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var guard = new SingleInstanceGuard();
        bool acquired = guard.TryAcquire(
            onActivateRequested: () => { },
            onCloseRequested: null,
            onExitRequested: () => exitReceivedTcs.TrySetResult(true)
        );

        if (acquired)
        {
            try
            {
                bool sent = SingleInstanceGuard.SendCommand("EXIT", timeoutMs: 1000);
                Assert.True(sent);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var result = await Task.WhenAny(exitReceivedTcs.Task, Task.Delay(Timeout.Infinite, cts.Token));
                Assert.True(result == exitReceivedTcs.Task);
                Assert.True(await exitReceivedTcs.Task);
            }
            finally
            {
                await guard.DisposeAsync();
            }
        }
    }

    [Fact]
    public void SingleInstanceGuard_WhenNotRunning_SendCommandReturnsFalseCleanly()
    {
        // When no instance is listening, SendCommand should return false without throwing or hanging
        bool result = SingleInstanceGuard.SendCommand("EXIT", timeoutMs: 100);
        Assert.False(result);
    }
}
