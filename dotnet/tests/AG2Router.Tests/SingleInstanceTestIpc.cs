using AG2Router.Windows.Lifecycle;

namespace AG2Router.Tests;

internal static class SingleInstanceTestIpc
{
    public static SingleInstanceIpcNamespace CreateNamespace()
    {
        string id = Guid.NewGuid().ToString("N");
        return new SingleInstanceIpcNamespace(
            @"Local\AG2Router_Test_Mutex_" + id, "AG2Router_Test_IPC_" + id);
    }
}
