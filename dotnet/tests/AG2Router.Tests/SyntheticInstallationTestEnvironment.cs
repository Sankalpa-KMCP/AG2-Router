using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AG2Router.Tests;

/// <summary>
/// Managed test harness for synthetic installation and uninstallation tests (R01).
/// </summary>
/// <remarks>
/// <para>
/// <b>Isolation Invariants:</b>
/// <list type="bullet">
/// <item><description>All operations execute in an isolated temp folder (<see cref="Root"/>).</description></item>
/// <item><description>Host environment variables (<c>AG2_LIVE_TEST = 0</c>, <c>AG2_RUN_LIVE_WINCRED_TESTS = false</c>) are strictly enforced.</description></item>
/// <item><description>PowerShell child processes are launched with <c>-NoProfile</c> and <c>-NonInteractive</c> using base64 encoded scripts.</description></item>
/// <item><description>Any attempted access to production registry, live processes, or external directories triggers synthetic boundary errors and fails closed.</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class SyntheticInstallationTestEnvironment : IDisposable
{
    internal const string UninstallKey = @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AG2Router";
    internal const string InnoKey = @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{D37E7404-585A-4B6A-B7F9-5360980DF628}_is1";
    internal const string RunKey = @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"AG2_SyntheticInstall_{Guid.NewGuid():N}");
    public string Target => Path.Combine(Root, "LocalApp", "Programs", "AG2Router");
    public string Exe => Path.Combine(Target, "AG2Router.exe");
    public string StartMenuShortcut => Path.Combine(Root, "StartMenu", "AG2 Router.lnk");
    public string DesktopShortcut => Path.Combine(Root, "Desktop", "AG2 Router.lnk");
    public string UserData => Path.Combine(Root, "LocalApp", "AG2-Router");

    public SyntheticInstallationTestEnvironment(string repositoryRoot)
    {
        foreach (string directory in new[] { "Scripts", "SourcePayload", "StartMenu", "Desktop", "LocalApp" })
            Directory.CreateDirectory(Path.Combine(Root, directory));
        foreach (string kind in new[] { "install", "uninstall" })
            File.Copy(Path.Combine(repositoryRoot, "scripts", kind + ".ps1"), Path.Combine(Root, "Scripts", kind + ".ps1"));
        File.Copy(Path.Combine(repositoryRoot, "dotnet", "tests", "AG2Router.Tests", "Fixtures", "SyntheticInstallationEnvironment.ps1"),
            Path.Combine(Root, "Scripts", "environment.ps1"));
        File.WriteAllText(Path.Combine(Root, "SourcePayload", "AG2Router.exe"), "fresh-payload");
        File.Copy(Path.Combine(Root, "Scripts", "uninstall.ps1"), Path.Combine(Root, "SourcePayload", "uninstall.ps1"));
    }

    public void SeedInstallation(bool inno = false)
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Exe, "existing-payload");
        File.Copy(Path.Combine(Root, "Scripts", "uninstall.ps1"), Path.Combine(Target, "uninstall.ps1"));
        if (inno)
        {
            File.WriteAllText(Path.Combine(Target, "unins000.exe"), "inno-uninstaller");
            File.WriteAllText(Path.Combine(Target, "unins000.dat"), "inno-data");
        }
    }

    public void AppendScript(string kind, string text) => File.AppendAllText(Path.Combine(Root, "Scripts", kind + ".ps1"), text);

    public static string OwnedPowerShellRegistry => $$"""
        $global:AG2Synthetic.Registry['{{UninstallKey}}'] = @{
            DisplayName = 'AG2 Router'; Publisher = 'AG2'; InstallLocation = $target
            UninstallString = "powershell.exe -File `"$target\uninstall.ps1`""
        }
        """;

    public static string OwnedInnoRegistry => $$"""
        $global:AG2Synthetic.Registry['{{InnoKey}}'] = @{
            DisplayName = 'AG2 Router'; Publisher = 'AG2'; InstallLocation = $target
            UninstallString = "`"$target\unins000.exe`""
        }
        """;

    public async Task<Result> RunAsync(string kind, string setup = "", string extraParameters = "@{}")
    {
        string snapshotPath = Path.Combine(Root, "snapshot.json");
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{Quote(Path.Combine(Root, "Scripts", "environment.ps1"))}}' -Root '{{Quote(Root)}}'
            $target = '{{Quote(Target)}}'
            $code = 0
            try {
                {{setup}}
                Invoke-SyntheticInstallation -Kind '{{kind}}' -ExtraParameters {{extraParameters}}
            } catch {
                $code = 1
                Write-Output $_.Exception.Message
            } finally {
                if ($global:AG2Synthetic.BoundaryErrors.Count) { $code = 1 }
                [IO.File]::WriteAllText('{{Quote(snapshotPath)}}', ($global:AG2Synthetic | ConvertTo-Json -Depth 12))
            }
            exit $code
            """;
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Root
        };
        start.Environment["AG2_LIVE_TEST"] = "0";
        start.Environment["AG2_RUN_LIVE_WINCRED_TESTS"] = "false";
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Synthetic PowerShell interpreter did not start.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException)
        {
            // Only this fixture's interpreter is owned; no application lookup or shutdown is used.
            child.Kill();
            await child.WaitForExitAsync();
            throw;
        }
        return new Result(child.ExitCode, await stdout + await stderr,
            JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath)));
    }

    private static string Quote(string text) => text.Replace("'", "''");

    public void Dispose()
    {
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        string resolved = Path.GetFullPath(Root);
        if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("AG2_SyntheticInstall_", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the task-owned fixture.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }

    internal sealed record Result(int ExitCode, string Output, JsonDocument Snapshot) : IDisposable
    {
        public JsonElement State => Snapshot.RootElement;
        public string[] Operations => State.GetProperty("Operations").EnumerateArray().Select(x => x.GetString()!).ToArray();
        public string[] FilePaths => State.GetProperty("FilePaths").EnumerateArray().Select(x => x.GetString()!).ToArray();
        public string[] BoundaryErrors => State.GetProperty("BoundaryErrors").EnumerateArray().Select(x => x.GetString()!).ToArray();
        public void Dispose() => Snapshot.Dispose();
    }
}
