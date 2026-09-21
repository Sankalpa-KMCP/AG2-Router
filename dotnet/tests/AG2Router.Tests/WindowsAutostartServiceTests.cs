using AG2Router.Windows.Lifecycle;
using Xunit;

namespace AG2Router.Tests;

public class WindowsAutostartServiceTests
{
    [Fact]
    public void Autostart_InitiallyDisabled()
    {
        var fakeRegistry = new InMemoryRegistryAccessor();
        var service = new WindowsRegistryAutostartService(fakeRegistry, @"C:\Tools\AG2Router.exe");

        Assert.False(service.IsAutostartEnabled());
        Assert.Equal(@"C:\Tools\AG2Router.exe", service.ExecutablePath);
    }

    [Fact]
    public void Autostart_EnableSetsExactQuotedCommandWithTrayFlag()
    {
        var fakeRegistry = new InMemoryRegistryAccessor();
        var service = new WindowsRegistryAutostartService(fakeRegistry, @"C:\Tools\AG2Router.exe");

        service.SetAutostartEnabled(true);

        Assert.True(service.IsAutostartEnabled());
        string? regValue = fakeRegistry.GetStringValue(
            WindowsRegistryAutostartService.RunSubKey,
            WindowsRegistryAutostartService.ValueName
        );

        Assert.Equal("\"C:\\Tools\\AG2Router.exe\" --tray", regValue);
    }

    [Fact]
    public void Autostart_DisableRemovesRegistryValue()
    {
        var fakeRegistry = new InMemoryRegistryAccessor();
        var service = new WindowsRegistryAutostartService(fakeRegistry, @"C:\Tools\AG2Router.exe");

        service.SetAutostartEnabled(true);
        Assert.True(service.IsAutostartEnabled());

        service.SetAutostartEnabled(false);
        Assert.False(service.IsAutostartEnabled());

        string? regValue = fakeRegistry.GetStringValue(
            WindowsRegistryAutostartService.RunSubKey,
            WindowsRegistryAutostartService.ValueName
        );
        Assert.Null(regValue);
    }

    [Fact]
    public void Autostart_OperationsAreIdempotent()
    {
        var fakeRegistry = new InMemoryRegistryAccessor();
        var service = new WindowsRegistryAutostartService(fakeRegistry, @"C:\Tools\AG2Router.exe");

        // Double enable
        service.SetAutostartEnabled(true);
        service.SetAutostartEnabled(true);
        Assert.True(service.IsAutostartEnabled());

        // Double disable
        service.SetAutostartEnabled(false);
        service.SetAutostartEnabled(false);
        Assert.False(service.IsAutostartEnabled());
    }
}
