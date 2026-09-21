namespace AG2Router.Windows.Lifecycle;

public class WindowsRegistryAutostartService : IAutostartService
{
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "AG2Router";

    private readonly IRegistryAccessor _registry;
    private readonly string _executablePath;

    public WindowsRegistryAutostartService(
        IRegistryAccessor registry,
        string? executablePath = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _executablePath = executablePath ?? Environment.ProcessPath ?? "AG2Router.exe";
    }

    public string ExecutablePath => _executablePath;

    public bool IsAutostartEnabled()
    {
        string? value = _registry.GetStringValue(RunSubKey, ValueName);
        return !string.IsNullOrWhiteSpace(value);
    }

    public void SetAutostartEnabled(bool enabled)
    {
        if (enabled)
        {
            _registry.SetStringValue(RunSubKey, ValueName, $"\"{_executablePath}\" --tray");
        }
        else
        {
            _registry.DeleteValue(RunSubKey, ValueName);
        }
    }
}
