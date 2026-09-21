namespace AG2Router.Windows.Lifecycle;

public interface IRegistryAccessor
{
    string? GetStringValue(string subKeyPath, string valueName);
    void SetStringValue(string subKeyPath, string valueName, string value);
    void DeleteValue(string subKeyPath, string valueName);
}

public class WindowsRegistryAccessor : IRegistryAccessor
{
    public string? GetStringValue(string subKeyPath, string valueName)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void SetStringValue(string subKeyPath, string valueName, string value)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
        key?.SetValue(valueName, value, Microsoft.Win32.RegistryValueKind.String);
    }

    public void DeleteValue(string subKeyPath, string valueName)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

public class InMemoryRegistryAccessor : IRegistryAccessor
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public string? GetStringValue(string subKeyPath, string valueName)
    {
        lock (_lock)
        {
            string key = $"{subKeyPath}\\{valueName}";
            return _values.TryGetValue(key, out string? value) ? value : null;
        }
    }

    public void SetStringValue(string subKeyPath, string valueName, string value)
    {
        lock (_lock)
        {
            string key = $"{subKeyPath}\\{valueName}";
            _values[key] = value;
        }
    }

    public void DeleteValue(string subKeyPath, string valueName)
    {
        lock (_lock)
        {
            string key = $"{subKeyPath}\\{valueName}";
            _values.Remove(key);
        }
    }
}
