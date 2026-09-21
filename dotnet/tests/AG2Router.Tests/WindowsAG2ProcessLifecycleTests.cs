using System.IO;
using AG2Router.AG2.Discovery;
using AG2Router.AG2.Switching;
using Xunit;

namespace AG2Router.Tests;

public sealed class WindowsAG2ProcessLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ag2_process_{Guid.NewGuid():N}");

    public WindowsAG2ProcessLifecycleTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task CaptureVerifiedPreservesRawArgumentsButRedactsSensitiveDisplayArguments()
    {
        string executable = Path.Combine(_root, "antigravity", "resources", "bin", "language_server.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllBytesAsync(executable, [0x4d, 0x5a]);
        var raw = new DiscoveredProcessRaw(
            404,
            "language_server.exe",
            $"\"{executable}\" --standalone --csrf_token csrf-secret --host_bridge_token=bridge-secret --password pass-secret",
            executable,
            DateTime.UtcNow.AddMinutes(-1));
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>([raw])
        };
        var detector = new AG2ProcessDetector(inspector: inspector);
        var lifecycle = new WindowsAG2ProcessLifecycle(detector, inspector);

        var snapshot = await lifecycle.CaptureVerifiedAsync();

        Assert.Contains("csrf-secret", snapshot.Arguments);
        string safe = string.Join(' ', snapshot.SanitizedArguments);
        Assert.DoesNotContain("csrf-secret", safe);
        Assert.DoesNotContain("bridge-secret", safe);
        Assert.DoesNotContain("pass-secret", safe);
        Assert.Contains("[REDACTED]", safe);
    }

    [Fact]
    public async Task RevalidateRejectsPidReuseWithDifferentStartTime()
    {
        string executable = Path.Combine(_root, "antigravity", "resources", "bin", "language_server.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllBytesAsync(executable, [0x4d, 0x5a]);
        DateTime firstStart = DateTime.UtcNow.AddMinutes(-2);
        var current = new DiscoveredProcessRaw(
            505, "language_server.exe", $"\"{executable}\" --standalone --csrf_token synthetic",
            executable, firstStart);
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>([current])
        };
        var lifecycle = new WindowsAG2ProcessLifecycle(new AG2ProcessDetector(inspector: inspector), inspector);
        var snapshot = await lifecycle.CaptureVerifiedAsync();
        current = current with { StartTime = firstStart.AddMinutes(1) };

        await Assert.ThrowsAsync<AG2ProcessLifecycleException>(() => lifecycle.RevalidateAsync(snapshot));
    }
}
