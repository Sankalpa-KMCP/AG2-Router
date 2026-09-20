namespace AG2Router.Windows.Lifecycle;

public interface IAutostartService
{
    bool IsAutostartEnabled();
    void SetAutostartEnabled(bool enabled);
}

/// <summary>
/// Harmless settings-only placeholder for autostart configuration.
/// Performs zero Registry, Startup folder, or Task Scheduler mutations in the foundation PR.
/// </summary>
public class DummyAutostartService : IAutostartService
{
    private bool _enabled = false;

    public bool IsAutostartEnabled() => _enabled;

    public void SetAutostartEnabled(bool enabled)
    {
        _enabled = enabled;
    }
}
